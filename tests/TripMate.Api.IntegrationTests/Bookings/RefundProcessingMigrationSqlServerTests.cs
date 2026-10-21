using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Bookings;

public sealed class RefundProcessingMigrationSqlServerTests
{
    private const string MigrationFileName = "20261021_add_refund_processing_lifecycle.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_UpgradesLegacyRefunds_BackfillsPendingAndCanRunAgain()
    {
        await using SqlServerTestDatabase database = await CreateLegacyDatabaseAsync();

        await ApplyAsync(database);
        await AssertShapeAsync(database);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment.Refunds WHERE refund_id = 1 AND next_attempt_at IS NOT NULL;"))
            .Should().Be(1);

        await ApplyAsync(database);
        await AssertShapeAsync(database);
    }

    private static async Task<SqlServerTestDatabase> CreateLegacyDatabaseAsync()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commerce;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.Bookings (booking_id BIGINT NOT NULL PRIMARY KEY);
            CREATE TABLE payment.Refunds (
                refund_id BIGINT NOT NULL PRIMARY KEY,
                booking_id BIGINT NOT NULL,
                transaction_id BIGINT NULL,
                cancellation_operation_id BIGINT NULL,
                reason NVARCHAR(500) NULL,
                amount DECIMAL(12,2) NOT NULL,
                initiated_by VARCHAR(14) NOT NULL,
                status VARCHAR(10) NOT NULL CONSTRAINT CK_LegacyRefundStatus CHECK (status IN ('Pending','Processed','Failed')),
                requested_at DATETIME2 NOT NULL,
                processed_at DATETIME2 NULL
            );
            CREATE TABLE payment.PaymentTransactions (transaction_id BIGINT NOT NULL PRIMARY KEY, booking_id BIGINT NOT NULL);
            INSERT INTO commerce.Bookings (booking_id) VALUES (100);
            INSERT INTO payment.Refunds (refund_id, booking_id, amount, initiated_by, status, requested_at)
            VALUES (1, 100, 50000, 'System', 'Pending', SYSUTCDATETIME());
            """);
        return database;
    }

    private static Task ApplyAsync(SqlServerTestDatabase database) => database.ExecuteScriptAsync(Path.Combine(
        AppContext.BaseDirectory, "Database", "migrations", MigrationFileName));

    private static async Task AssertShapeAsync(SqlServerTestDatabase database)
    {
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'payment.Refunds')
            AND name IN (N'gateway_reference',N'attempt_count',N'last_attempt_at',N'next_attempt_at',N'last_error_code',N'last_error_message');
            """)).Should().Be(6);
        (await database.ExecuteScalarAsync<short>("""
            SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID(N'payment.Refunds')
            AND name = N'status';
            """)).Should().Be(20, "RetryPending must fit in the upgraded status column");
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'payment.Refunds')
            AND name IN (N'CK_Refunds_Status',N'CK_Refunds_AttemptCount');
            """)).Should().Be(2);
        (await database.ExecuteScalarAsync<string>("""
            SELECT definition FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Refunds') AND name = N'CK_Refunds_Status';
            """)).Should().Contain("ManualReview");
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'payment.Refunds') AND name = N'IX_Refunds_Due';
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'payment.PaymentTransactions')
            AND name = N'FK_PaymentTransactions_Refund';
            """)).Should().Be(1);
    }
}