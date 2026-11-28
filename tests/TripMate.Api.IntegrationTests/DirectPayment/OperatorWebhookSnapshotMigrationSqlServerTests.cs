using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.DirectPayment;

public sealed class OperatorWebhookSnapshotMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SnapshotMigrationIsRepeatablePreservesLegacyAndRejectsPartialBindings()
    {
        // Minimal isolated database: no restore of the application's live database or financial data.
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE SCHEMA payment;
            """);
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE payment.OperatorPaymentChannels (channel_id BIGINT NOT NULL PRIMARY KEY);
            CREATE TABLE payment.PaymentTransactions (transaction_id BIGINT NOT NULL PRIMARY KEY, amount DECIMAL(12,2) NOT NULL);
            INSERT payment.OperatorPaymentChannels VALUES (7);
            INSERT payment.PaymentTransactions VALUES (1, 123.45);
            """);
        var migration = Path.Combine(AppContext.BaseDirectory, "Database", "migrations", "20261125_operator_payos_webhook_snapshots.sql");
        await database.ExecuteScriptAsync(migration);
        await database.ExecuteScriptAsync(migration);
        (await database.ExecuteScalarAsync<decimal>("SELECT amount FROM payment.PaymentTransactions WHERE transaction_id = 1")).Should().Be(123.45m);
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment.PaymentTransactions WHERE operator_channel_id IS NULL AND operator_checksum_key_ref IS NULL")).Should().Be(1);
        await database.ExecuteNonQueryAsync("UPDATE payment.PaymentTransactions SET operator_channel_id = 7, operator_channel_version = 2, operator_checksum_key_ref = N'encrypted-test-reference' WHERE transaction_id = 1");
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("UPDATE payment.PaymentTransactions SET operator_checksum_key_ref = NULL WHERE transaction_id = 1"));
        (await database.ExecuteScalarAsync<int>("SELECT operator_channel_version FROM payment.PaymentTransactions WHERE transaction_id = 1")).Should().Be(2);
        await database.ExecuteNonQueryAsync("INSERT payment.OperatorPaymentMerchantClaims (merchant_fingerprint, channel_id, created_at) VALUES (REPLICATE('a', 64), 7, SYSUTCDATETIME())");
        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteNonQueryAsync("INSERT payment.OperatorPaymentMerchantClaims (merchant_fingerprint, channel_id, created_at) VALUES (REPLICATE('a', 64), 7, SYSUTCDATETIME())"));
    }
}