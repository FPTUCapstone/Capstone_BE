using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Tickets;

public sealed class TicketQrRevocationSqlServerTests
{
    private static readonly DateTimeOffset Departure = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Revoke_ChangesOnlyTheActiveCodesOfThatBooking()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var (bookingId, activeId, oldId) = await SeedTicketWithCodesAsync(seed);
        var (_, otherActiveId, _) = await SeedTicketWithCodesAsync(seed);

        int affected;
        await using (var context = database.CreateDbContext())
        {
            affected = await context.RevokeActiveTicketQrCodesAsync(bookingId, Now, CancellationToken.None);
        }

        affected.Should().Be(1);
        (await StatusOfAsync(database, activeId)).Should().Be(nameof(TicketQrCodeStatus.Revoked));
        (await StatusOfAsync(database, oldId)).Should().Be(nameof(TicketQrCodeStatus.Consumed));
        (await StatusOfAsync(database, otherActiveId)).Should().Be(nameof(TicketQrCodeStatus.Active));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Revoke_IsRolledBackWithTheSurroundingTransaction()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var (bookingId, activeId, _) = await SeedTicketWithCodesAsync(seed);

        await using (var context = database.CreateDbContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            (await context.RevokeActiveTicketQrCodesAsync(bookingId, Now, CancellationToken.None)).Should().Be(1);
            await transaction.RollbackAsync();
        }

        (await StatusOfAsync(database, activeId)).Should().Be(nameof(TicketQrCodeStatus.Active));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Revoke_WithoutCodes_ReturnsZero()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await TicketSqlServerSeed.CreateAsync(database, Departure);
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);

        await using var context = database.CreateDbContext();

        (await context.RevokeActiveTicketQrCodesAsync(bookingId, Now, CancellationToken.None)).Should().Be(0);
    }

    private static async Task<(long BookingId, long ActiveId, long ConsumedId)> SeedTicketWithCodesAsync(
        TicketSqlServerSeed seed)
    {
        var bookingId = await seed.AddBookingAsync(BookingStatus.Confirmed, PaymentStatus.Paid);
        var ticketId = await seed.AddTicketAsync(bookingId);
        var consumedId = await InsertCodeAsync(seed.Database, ticketId, TicketQrCodeStatus.Consumed);
        var activeId = await InsertCodeAsync(seed.Database, ticketId, TicketQrCodeStatus.Active);
        return (bookingId, activeId, consumedId);
    }

    private static Task<long> InsertCodeAsync(SqlServerTestDatabase database, long ticketId,
        TicketQrCodeStatus status) =>
        database.ExecuteScalarAsync<long>($"""
            INSERT INTO commerce.TicketQrCodes (ticket_id, qr_token, signature, status)
            OUTPUT INSERTED.qr_code_id
            VALUES ({ticketId}, '{Guid.NewGuid():N}', 'signature', '{status}');
            """);

    private static Task<string> StatusOfAsync(SqlServerTestDatabase database, long codeId) =>
        database.ExecuteScalarAsync<string>(
            $"SELECT status FROM commerce.TicketQrCodes WHERE qr_code_id = {codeId};");
}
