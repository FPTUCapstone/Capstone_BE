using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Payouts;

/// <summary>
/// DAILY-PAYOUT-TRANSFER plan Task 2A (spec v6, Q4/Q5): schema for Advance/Final settlements,
/// the Resolved status, the auto-lock columns on dbo.Users and the TourOperatorDebt ledger.
/// The migration must refuse to run while legacy Main/Retained rows exist; those rows are moved
/// out by 20261112_daily_payout_v6_archive_main_rows.sql (Task 2B) which runs first.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class DailyPayoutV6SchemaMigrationSqlServerTests
{
    private const string FoundationFileName = "20261108_daily_payout_settlement_foundation.sql";
    private const string SchemaFileName = "20261112_daily_payout_v6_schema.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RunTwiceOnEmptyDatabase_IsIdempotent()
    {
        await using var database = await CreateBaseDatabaseAsync();

        await database.ExecuteScriptAsync(MigrationPath(SchemaFileName));
        Func<Task> rerun = () => database.ExecuteScriptAsync(MigrationPath(SchemaFileName));

        await rerun.Should().NotThrowAsync();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_KindConstraintAcceptsAdvanceAndFinalAndRejectsLegacyKinds()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath(SchemaFileName));
        await SeedBookingAndOperatorAsync(database);

        await database.ExecuteNonQueryAsync(SettlementInsertSql(bookingId: 1, kind: "Advance", status: "Scheduled"));
        await database.ExecuteNonQueryAsync(SettlementInsertSql(bookingId: 1, kind: "Final", status: "Scheduled"));

        Func<Task> legacyMain = () => database.ExecuteNonQueryAsync(
            SettlementInsertSql(bookingId: 1, kind: "Main", status: "Scheduled"));
        Func<Task> legacyRetained = () => database.ExecuteNonQueryAsync(
            SettlementInsertSql(bookingId: 1, kind: "Retained", status: "Scheduled"));

        await legacyMain.Should().ThrowAsync<SqlException>();
        await legacyRetained.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_StatusConstraintAcceptsResolvedAndRejectsCarriedOver()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath(SchemaFileName));
        await SeedBookingAndOperatorAsync(database);

        await database.ExecuteNonQueryAsync(SettlementInsertSql(bookingId: 1, kind: "Final", status: "Resolved"));

        Func<Task> carriedOver = () => database.ExecuteNonQueryAsync(
            SettlementInsertSql(bookingId: 1, kind: "Advance", status: "Carried Over"));

        await carriedOver.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AddsAutoLockColumnsToUsersWithDatetime2Timestamp()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath(SchemaFileName));

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.Users') AND name = N'lock_reason'
              AND system_type_id = TYPE_ID(N'nvarchar') AND max_length = 400 AND is_nullable = 1;
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dbo.Users') AND name = N'locked_at_utc'
              AND system_type_id = TYPE_ID(N'datetime2') AND is_nullable = 1;
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_CreatesTourOperatorDebtLedgerWithDatetime2Timestamps()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath(SchemaFileName));

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE object_id = OBJECT_ID(N'payment.TourOperatorDebt', N'U');
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.TourOperatorDebt')
              AND name IN (N'incurred_at_utc', N'created_at_utc')
              AND system_type_id = TYPE_ID(N'datetime2');
            """)).Should().Be(2);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.TourOperatorDebt')
              AND name = N'CK_TourOperatorDebt_recovered_within_amount';
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RefusesToRunWhileLegacyMainRowsExist()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath(FoundationFileName));
        await SeedBookingAndOperatorAsync(database);
        await database.ExecuteNonQueryAsync(SettlementInsertSql(bookingId: 1, kind: "Main", status: "Settled"));

        Func<Task> migrate = () => database.ExecuteScriptAsync(MigrationPath(SchemaFileName));

        await migrate.Should().ThrowAsync<SqlException>()
            .Where(exception => exception.Message.Contains(
                "20261112_daily_payout_v6_archive_main_rows.sql",
                StringComparison.Ordinal));
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
            """);
        await database.ExecuteScriptAsync(MigrationPath(FoundationFileName));
        return database;
    }

    private static async Task SeedBookingAndOperatorAsync(SqlServerTestDatabase database)
    {
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users DEFAULT VALUES;
            INSERT commerce.Bookings DEFAULT VALUES;
            """);
    }

    private static string SettlementInsertSql(long bookingId, string kind, string status)
    {
        return $"""
            INSERT payment.PayoutSettlement (
                booking_id, tour_operator_user_id, run_date, kind, gross_amount, commission_rate,
                commission_amount, net_amount, payout_amount, status)
            VALUES ({bookingId}, 1, '2026-11-12', N'{kind}', 1000000, 10, 100000, 900000, 700000, N'{status}');
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
