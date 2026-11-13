using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Payouts;

/// <summary>
/// DAILY-PAYOUT-TRANSFER plan Task 2B (spec v6, Q3): archives every legacy Main/Retained
/// payment.PayoutSettlement row (and the gateway attempts of batches made up entirely of such
/// rows) into *_Archive_v3 tables, verifies the copy matches before deleting, and is a safe
/// no-op when no legacy rows remain. Must run BEFORE 20261112_daily_payout_v6_schema.sql, which
/// refuses to apply while any Main/Retained row still exists.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class DailyPayoutV6ArchiveMainRowsMigrationSqlServerTests
{
    private const string FoundationFileName = "20261108_daily_payout_settlement_foundation.sql";
    private const string ArchiveFileName = "20261112_daily_payout_v6_archive_main_rows.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_ArchivesMainRowsAndTheirBatchAttempts_ThenRemovesThemFromTheLiveTables()
    {
        await using var database = await CreateBaseDatabaseAsync();
        var batchId = await SeedBatchAsync(database, tourOperatorUserId: 1);
        var bookingAId = await SeedBookingAsync(database);
        var bookingBId = await SeedBookingAsync(database);
        await database.ExecuteNonQueryAsync(MainSettlementInsertSql(bookingAId, batchId, "Settled"));
        await database.ExecuteNonQueryAsync(MainSettlementInsertSql(bookingBId, batchId, "Settled"));
        await database.ExecuteNonQueryAsync(AttemptInsertSql(batchId, attemptNumber: 1));

        await database.ExecuteScriptAsync(MigrationPath(ArchiveFileName));

        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM payment.PayoutSettlement WHERE kind = N'Main';"))
            .Should().Be(0);
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM payment.PayoutSettlementAttempt WHERE batch_id = " + batchId + ";"))
            .Should().Be(0);
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM payment.PayoutSettlement_Archive_v3 WHERE kind = N'Main';"))
            .Should().Be(2);
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM payment.PayoutSettlementAttempt_Archive_v3 WHERE batch_id = " + batchId + ";"))
            .Should().Be(1);
        // The batch itself (PayoutBatch) is not touched by this migration (plan Task 2B scope).
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM payment.PayoutBatch WHERE id = " + batchId + ";"))
            .Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RunTwice_SecondRunIsANoOpAndKeepsTheArchiveUnchanged()
    {
        await using var database = await CreateBaseDatabaseAsync();
        var batchId = await SeedBatchAsync(database, tourOperatorUserId: 1);
        var bookingId = await SeedBookingAsync(database);
        await database.ExecuteNonQueryAsync(MainSettlementInsertSql(bookingId, batchId, "Settled"));
        await database.ExecuteNonQueryAsync(AttemptInsertSql(batchId, attemptNumber: 1));

        await database.ExecuteScriptAsync(MigrationPath(ArchiveFileName));
        Func<Task> rerun = () => database.ExecuteScriptAsync(MigrationPath(ArchiveFileName));

        await rerun.Should().NotThrowAsync();
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM payment.PayoutSettlement_Archive_v3 WHERE kind = N'Main';"))
            .Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_OnDatabaseWithNoLegacyRows_IsANoOp()
    {
        await using var database = await CreateBaseDatabaseAsync();

        Func<Task> migrate = () => database.ExecuteScriptAsync(MigrationPath(ArchiveFileName));

        await migrate.Should().NotThrowAsync();
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM payment.PayoutSettlement_Archive_v3;"))
            .Should().Be(0);
    }

    private static async Task<SqlServerTestDatabase> CreateBaseDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commerce;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY
            );
            CREATE TABLE commerce.Bookings (
                booking_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Bookings PRIMARY KEY
            );
            INSERT dbo.Users DEFAULT VALUES;
            """);
        await database.ExecuteScriptAsync(MigrationPath(FoundationFileName));
        return database;
    }

    private static async Task<long> SeedBatchAsync(SqlServerTestDatabase database, long tourOperatorUserId)
    {
        return await database.ExecuteScalarAsync<long>($"""
            INSERT payment.PayoutBatch (run_date, tour_operator_user_id, total_amount, status)
            OUTPUT INSERTED.id
            VALUES ('2026-11-10', {tourOperatorUserId}, 700000, N'Settled');
            """);
    }

    private static async Task<long> SeedBookingAsync(SqlServerTestDatabase database)
    {
        return await database.ExecuteScalarAsync<long>(
            "INSERT commerce.Bookings OUTPUT INSERTED.booking_id DEFAULT VALUES;");
    }

    private static string MainSettlementInsertSql(long bookingId, long batchId, string status) =>
        SettlementInsertSql(bookingId, batchId, kind: "Main", status);

    private static string SettlementInsertSql(long bookingId, long batchId, string kind, string status)
    {
        return $"""
            INSERT payment.PayoutSettlement (
                batch_id, booking_id, tour_operator_user_id, run_date, kind, gross_amount, commission_rate,
                commission_amount, net_amount, payout_amount, status)
            VALUES ({batchId}, {bookingId}, 1, '2026-11-10', N'{kind}', 1000000, 10, 100000, 900000, 700000, N'{status}');
            """;
    }

    private static string AttemptInsertSql(long batchId, int attemptNumber)
    {
        return $"""
            INSERT payment.PayoutSettlementAttempt (
                batch_id, attempt_number, sent_at_utc, gateway_reference, outcome)
            VALUES ({batchId}, {attemptNumber}, SYSUTCDATETIME(), N'SIM-TEST-{batchId}-{attemptNumber}', N'Succeeded');
            """;
    }

    private static string MigrationPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the repository root for the payout migration.");
        }

        return Path.Combine(directory.FullName, "database", "migrations", fileName);
    }
}