using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class LegacyPayoutRemovalMigrationSqlServerTests
{
    private const string MigrationFileName = "20261108_remove_legacy_payout_request_tables.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RemovesOrphanedSettlementWorkItemsBeforeLegacyPayouts_AndRerunsSafely()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.SystemConfigs (
                config_key NVARCHAR(100) NOT NULL CONSTRAINT PK_SystemConfigs PRIMARY KEY
            );

            CREATE TABLE payment.Payouts (
                payout_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payouts PRIMARY KEY
            );

            CREATE TABLE payment.PayoutSettlementWorkItems (
                work_item_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutSettlementWorkItems PRIMARY KEY,
                payout_id BIGINT NOT NULL,
                CONSTRAINT FK_PayoutSettlementWorkItems_Payout
                    FOREIGN KEY (payout_id) REFERENCES payment.Payouts(payout_id)
            );

            CREATE TABLE payment.PayoutAdjustments (
                adjustment_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutAdjustments PRIMARY KEY,
                applied_payout_id BIGINT NULL,
                CONSTRAINT FK_PayoutAdjustments_AppliedPayout
                    FOREIGN KEY (applied_payout_id) REFERENCES payment.Payouts(payout_id)
            );

            CREATE TABLE payment.PayoutRun (
                id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PayoutRun PRIMARY KEY
            );
            """);

        await database.ExecuteScriptAsync(MigrationPath());
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE object_id IN (
                OBJECT_ID(N'payment.PayoutSettlementWorkItems'),
                OBJECT_ID(N'payment.Payouts'));
            """)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE object_id IN (
                OBJECT_ID(N'payment.PayoutAdjustments'),
                OBJECT_ID(N'payment.PayoutRun'));
            """)).Should().Be(2);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'payment.PayoutAdjustments')
              AND referenced_object_id = OBJECT_ID(N'payment.Payouts');
            """)).Should().Be(0);
    }

    private static string MigrationPath()
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

        return Path.Combine(directory.FullName, "database", "migrations", MigrationFileName);
    }
}
