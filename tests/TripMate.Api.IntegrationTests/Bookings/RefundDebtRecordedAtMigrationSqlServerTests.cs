using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Bookings;

/// <summary>
/// UC-41/UC-42/UC-67 SRS-alignment (plan Task 8, spec v1.2, BR-144): adds the nullable
/// payment.Refunds.debt_recorded_at column the return-deadline scan uses to avoid recording the
/// same operator debt twice.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class RefundDebtRecordedAtMigrationSqlServerTests
{
    private const string MigrationFileName = "20261121_refund_debt_recorded_at.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RunTwice_IsIdempotent()
    {
        await using var database = await CreateBaseDatabaseAsync();

        await database.ExecuteScriptAsync(MigrationPath());
        Func<Task> rerun = () => database.ExecuteScriptAsync(MigrationPath());

        await rerun.Should().NotThrowAsync();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AddsDebtRecordedAtAsNullableDateTime2()
    {
        await using var database = await CreateBaseDatabaseAsync();

        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Refunds') AND name = N'debt_recorded_at'
              AND system_type_id = TYPE_ID(N'datetime2') AND is_nullable = 1;
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_LeavesExistingRefundRowUntouched()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT payment.Refunds (booking_id, amount, initiated_by, status, requested_at, attempt_count)
            VALUES (1, 100000, N'System', N'Processed', SYSUTCDATETIME(), 0);
            """);

        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment.Refunds WHERE status = N'Processed';"))
            .Should().Be(1);
    }

    private static async Task<SqlServerTestDatabase> CreateBaseDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE payment.Refunds (
                refund_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Refunds PRIMARY KEY,
                booking_id BIGINT NOT NULL,
                amount DECIMAL(12,2) NOT NULL CONSTRAINT CK_Refunds_amount CHECK (amount > 0),
                initiated_by VARCHAR(14) NOT NULL
                    CONSTRAINT CK_Refunds_InitiatedBy CHECK (initiated_by IN
                        (N'Traveler', N'Staff', N'Administrator', N'TourOperator', N'System')),
                status VARCHAR(30) NOT NULL,
                requested_at DATETIME2 NOT NULL,
                attempt_count INT NOT NULL CONSTRAINT DF_Refunds_attempt_count DEFAULT 0
            );
            """);
        return database;
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
            throw new DirectoryNotFoundException("Could not locate the repository root for the refund debt migration.");
        }

        return Path.Combine(directory.FullName, "database", "migrations", MigrationFileName);
    }
}