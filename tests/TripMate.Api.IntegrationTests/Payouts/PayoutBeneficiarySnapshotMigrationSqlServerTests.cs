using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutBeneficiarySnapshotMigrationSqlServerTests
{
    private const string MigrationFileName = "20261014_add_payout_beneficiary_snapshot.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AddsNullableSnapshotColumnsAndRunsTwice()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE payment.Payouts (
                payout_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payouts PRIMARY KEY,
                operator_user_id BIGINT NOT NULL,
                period_start DATE NOT NULL,
                period_end DATE NOT NULL,
                gross_revenue DECIMAL(14,2) NOT NULL,
                commission_amount DECIMAL(14,2) NOT NULL,
                net_amount DECIMAL(14,2) NOT NULL,
                status VARCHAR(24) NOT NULL CONSTRAINT DF_Payouts_Status DEFAULT 'PendingReview',
                requested_at DATETIME2 NULL
            );
            """);

        await database.ExecuteScriptAsync(MigrationPath());
        await database.ExecuteScriptAsync(MigrationPath());

        (await ColumnCountAsync(database)).Should().Be(3);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Payouts') AND name = N'CK_Payouts_BeneficiarySnapshot';
            """)).Should().Be(1);

        await database.ExecuteNonQueryAsync(InsertPayout("NULL, NULL, NULL"));
        await database.ExecuteNonQueryAsync(InsertPayout("N'Vietcombank', N'NGUYEN VAN A', '1234'"));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RejectsPartialOrNonDigitSnapshot()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE payment.Payouts (
                payout_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payouts PRIMARY KEY,
                operator_user_id BIGINT NOT NULL,
                period_start DATE NOT NULL,
                period_end DATE NOT NULL,
                gross_revenue DECIMAL(14,2) NOT NULL,
                commission_amount DECIMAL(14,2) NOT NULL,
                net_amount DECIMAL(14,2) NOT NULL,
                status VARCHAR(24) NOT NULL CONSTRAINT DF_Payouts_Status DEFAULT 'PendingReview',
                requested_at DATETIME2 NULL
            );
            """);
        await database.ExecuteScriptAsync(MigrationPath());

        Func<Task> partial = () => database.ExecuteNonQueryAsync(InsertPayout("N'Vietcombank', NULL, NULL"));
        await partial.Should().ThrowAsync<SqlException>();

        Func<Task> nonDigit = () => database.ExecuteNonQueryAsync(InsertPayout("N'Vietcombank', N'NGUYEN VAN A', '12A4'"));
        await nonDigit.Should().ThrowAsync<SqlException>();

        Func<Task> tooShort = () => database.ExecuteNonQueryAsync(InsertPayout("N'Vietcombank', N'NGUYEN VAN A', '12'"));
        await tooShort.Should().ThrowAsync<SqlException>();
    }

    private static string InsertPayout(string snapshotValues) => $"""
        INSERT payment.Payouts(operator_user_id, period_start, period_end, gross_revenue, commission_amount,
            net_amount, status, requested_at, beneficiary_bank_name, beneficiary_account_holder, beneficiary_account_last4)
        VALUES (1, '2026-10-01', '2026-10-31', 100, 10, 90, 'PendingReview', NULL, {snapshotValues});
        """;

    private static async Task<int> ColumnCountAsync(SqlServerTestDatabase database) =>
        await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Payouts')
              AND name IN (N'beneficiary_bank_name', N'beneficiary_account_holder', N'beneficiary_account_last4');
            """);

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