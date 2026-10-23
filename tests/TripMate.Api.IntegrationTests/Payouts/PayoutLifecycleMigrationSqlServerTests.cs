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

    /// <summary>
    /// D05/C18: a database holding true pre-TM-208 rows (old status vocabulary, no
    /// CK_Payouts_LifecycleState yet) must still be rejected — no automatic mapping is attempted
    /// — and the rejection must roll back cleanly, leaving the legacy schema and the row exactly
    /// as they were. This is the case the review flagged: CI only exercised an empty table.
    /// </summary>
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WithTrueLegacyData_ThrowsAndLeavesSchemaAndDataUntouched()
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
                requested_at DATETIME2 NULL
            );
            INSERT dbo.Users DEFAULT VALUES;
            INSERT payment.Payouts(operator_user_id, period_start, period_end, gross_revenue,
                commission_amount, net_amount, status, requested_at)
            VALUES (1, '2026-08-01', '2026-08-31', 1000000, 100000, 900000, 'Confirmed', '2026-09-01T00:00:00');
            """);

        Func<Task> migrate = () => database.ExecuteScriptAsync(MigrationPath());
        await migrate.Should().ThrowAsync<SqlException>()
            .Where(exception => exception.Message.Contains(
                "TM-208 requires payment.Payouts to be reset before migration",
                StringComparison.Ordinal));

        // Rolled back completely: the new lifecycle machinery never appeared...
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Payouts') AND name = N'CK_Payouts_LifecycleState';
            """)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Payouts') AND name = N'reviewed_by';
            """)).Should().Be(0);

        // ...and the legacy row is exactly as it was before the attempt.
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM payment.Payouts WHERE status = 'Confirmed' AND gross_revenue = 1000000;
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Payouts') AND name = N'CK__Payouts__status__legacy';
            """)).Should().Be(1);
    }

    /// <summary>
    /// Once TM-208 has run, any later run — even against a database that already has rows
    /// created under the new lifecycle (Staff review, approvals, rejections) — must be a safe
    /// no-op rather than throwing. The migration previously could not tell "already migrated,
    /// has new rows" apart from "never migrated, has legacy rows", and would block re-apply on
    /// any database holding data, including its own already-upgraded output.
    /// </summary>
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AlreadyApplied_RerunningAgainstNewLifecycleDataIsANoOp()
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
                requested_at DATETIME2 NULL
            );
            """);

        // First run: empty table, applies normally (already covered above, repeated here as the
        // precondition for the second run).
        await database.ExecuteScriptAsync(MigrationPath());

        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users DEFAULT VALUES;
            INSERT payment.Payouts(operator_user_id, period_start, period_end, gross_revenue,
                commission_amount, net_amount, status, requested_at, reviewed_by, reviewed_at)
            VALUES (1, '2026-09-01', '2026-09-30', 12000000, 1200000, 10800000, 'ApprovedForSettlement',
                '2026-10-03T08:15:00', 1, '2026-10-14T09:00:00');
            """);

        // Second run against the now-populated, already-upgraded database: must not throw.
        Func<Task> rerun = () => database.ExecuteScriptAsync(MigrationPath());
        await rerun.Should().NotThrowAsync();

        // The row survives untouched.
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM payment.Payouts
            WHERE status = 'ApprovedForSettlement' AND gross_revenue = 12000000;
            """)).Should().Be(1);

        // Schema is still exactly the canonical lifecycle shape — no duplicated constraints from
        // a partial second application.
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'payment.Payouts') AND name = N'CK_Payouts_LifecycleState';
            """)).Should().Be(1);
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