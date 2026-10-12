using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutLifecycleMigrationSqlServerTests
{
    private const string MigrationFileName = "20261012_add_payout_lifecycle_foundation.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_ReplacesLegacyStatusConstraintWithoutDroppingMoneyConstraints()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY
            );
            CREATE TABLE payment.Payouts (
                payout_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Payouts PRIMARY KEY,
                operator_user_id BIGINT NOT NULL,
                period_start DATE NOT NULL,
                period_end DATE NOT NULL,
                gross_revenue DECIMAL(18,2) NOT NULL,
                commission_amount DECIMAL(18,2) NOT NULL,
                net_amount DECIMAL(18,2) NOT NULL,
                status VARCHAR(24) NOT NULL CONSTRAINT CK__Payouts__status__legacy
                    CHECK (status IN ('Pending', 'Requested', 'Confirmed', 'Paid', 'Rejected')),
                requested_at DATETIME2 NULL,
                CONSTRAINT CK__Payouts__gross_legacy CHECK (gross_revenue >= 0),
                CONSTRAINT CK__Payouts__commission_legacy CHECK (commission_amount >= 0),
                CONSTRAINT CK__Payouts__net_legacy CHECK (net_amount >= 0)
            );
            """);

        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Payouts')
              AND name = N'CK__Payouts__status__legacy';
            """)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Payouts')
              AND name = N'CK_Payouts_Status';
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Payouts')
              AND name IN (N'CK__Payouts__gross_legacy', N'CK__Payouts__commission_legacy', N'CK__Payouts__net_legacy');
            """)).Should().Be(3);

        Func<Task> insertNegativeGross = () => database.ExecuteNonQueryAsync("""
            INSERT payment.Payouts(operator_user_id, period_start, period_end, gross_revenue,
                commission_amount, net_amount, status, requested_at, reviewed_by, reviewed_at,
                rejection_reason, settlement_authorized_by, settlement_authorized_at,
                gateway_reference, gateway_failure_reason, gateway_attempt_count, last_gateway_attempt_at)
            VALUES (1, '2026-10-01', '2026-10-31', -1, 0, -1, 'PendingReview', NULL,
                NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL);
            """);
        await insertNegativeGross.Should().ThrowAsync<SqlException>();
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