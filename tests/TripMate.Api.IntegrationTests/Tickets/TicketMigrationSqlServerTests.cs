using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tickets;

public sealed class TicketMigrationSqlServerTests
{
    private const string IssuanceMigration = "20261024_uc29_1_ticket_issuance.sql";
    private const string BackfillMigration = "20261024_uc29_2_backfill_tickets.sql";
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task IssuanceMigration_CreatesOneTicketPerBookingIndexAndCanRunAgain()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await ApplyAsync(database, IssuanceMigration);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'commerce.Tickets')
              AND i.name = N'UX_Tickets_OneTicketPerBooking'
              AND i.is_unique = 1 AND i.has_filter = 0 AND c.name = N'booking_id';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task IssuanceMigration_WhenABookingHasSeveralTickets_RefusesAndNamesIt()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(
            "DROP INDEX UX_Tickets_OneTicketPerBooking ON commerce.Tickets;");
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO commerce.Tickets (booking_id, ticket_code, status)
            VALUES ({bookingId}, 'LEGACY-A-{bookingId}', 'Valid'), ({bookingId}, 'LEGACY-B-{bookingId}', 'Used');
            """);

        Func<Task> action = () => ApplyAsync(database, IssuanceMigration);

        var exception = await action.Should().ThrowAsync<SqlException>();
        exception.Which.Message.Should().Contain("one ticket per booking").And.Contain(bookingId.ToString());
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'commerce.Tickets') AND name = N'UX_Tickets_OneTicketPerBooking';
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task IssuanceMigration_WhenTheIndexHasAnotherShape_Refuses()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            DROP INDEX UX_Tickets_OneTicketPerBooking ON commerce.Tickets;
            CREATE INDEX UX_Tickets_OneTicketPerBooking ON commerce.Tickets(booking_id);
            """);

        Func<Task> action = () => ApplyAsync(database, IssuanceMigration);

        (await action.Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("incompatible shape");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Backfill_IssuesOneValidTicketForEachPaidConfirmedOrCompletedTourBookingOnly()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var confirmed = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var completed = await seed.AddBookingAsync(BookingStatus.Completed, PaymentStatus.Paid);
        var alreadyTicketed = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var existingTicket = await seed.AddTicketAsync(alreadyTicketed, TicketStatus.Used);
        var pending = await seed.AddBookingAsync(BookingStatus.PendingPayment, PaymentStatus.Unpaid);
        var expired = await seed.AddBookingAsync(BookingStatus.Expired, PaymentStatus.Unpaid);
        var cancelled = await seed.AddBookingAsync(BookingStatus.Cancelled, PaymentStatus.Refunded);
        var confirmedUnpaid = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Unpaid);
        var itineraryOnly = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        await database.ExecuteNonQueryAsync(
            $"UPDATE commerce.Bookings SET tour_schedule_id = NULL WHERE booking_id = {itineraryOnly};");

        await ApplyAsync(database, BackfillMigration);

        foreach (var bookingId in new[] { confirmed, completed })
        {
            (await database.ExecuteScalarAsync<string>($"""
                SELECT CONCAT(ticket_code, '|', status, '|', IIF(participant_id IS NULL, 'null', 'set'),
                              '|', IIF(expires_at IS NULL, 'null', 'set'), '|', IIF(issued_at IS NULL, 'null', 'set'))
                FROM commerce.Tickets WHERE booking_id = {bookingId};
                """)).Should().Be($"{Ticket.CodeFor(bookingId)}|Valid|null|null|set");
        }

        (await database.ExecuteScalarAsync<long>(
                $"SELECT ticket_id FROM commerce.Tickets WHERE booking_id = {alreadyTicketed};"))
            .Should().Be(existingTicket, "a booking that already holds a ticket is left alone");
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM commerce.Tickets
            WHERE booking_id IN ({pending}, {expired}, {cancelled}, {confirmedUnpaid}, {itineraryOnly});
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Backfill_RunTwice_InsertsNothingTheSecondTime()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);

        await ApplyAsync(database, BackfillMigration);
        var afterFirstRun = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commerce.Tickets;");
        await ApplyAsync(database, BackfillMigration);

        afterFirstRun.Should().Be(1);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM commerce.Tickets;")).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Backfill_CodesMatchTheDomainRuleForLargeIds()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        foreach (var bookingId in new[] { 1L, 456L, 9_999_999_999L, 12_345_678_901L, long.MaxValue })
        {
            (await database.ExecuteScalarAsync<string>($"""
                SELECT 'TK-' + CASE WHEN CAST({bookingId} AS BIGINT) < 10000000000
                                    THEN RIGHT('0000000000' + CAST(CAST({bookingId} AS BIGINT) AS VARCHAR(19)), 10)
                                    ELSE CAST(CAST({bookingId} AS BIGINT) AS VARCHAR(19)) END;
                """)).Should().Be(Ticket.CodeFor(bookingId));
        }

        var script = Collapse(await File.ReadAllTextAsync(MigrationPath(BackfillMigration)));
        script.Should().Contain(Collapse("""
            'TK-' + CASE WHEN b.booking_id < 10000000000
                         THEN RIGHT('0000000000' + CAST(b.booking_id AS VARCHAR(19)), 10)
                         ELSE CAST(b.booking_id AS VARCHAR(19)) END
            """), "the expression asserted above must be the one the backfill uses");
    }

    private static string Collapse(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static Task ApplyAsync(SqlServerTestDatabase database, string fileName) =>
        database.ExecuteScriptAsync(MigrationPath(fileName));

    private static string MigrationPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Database", "migrations", fileName);
}
