using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Bookings;

/// <summary>
/// UC-41/UC-42/UC-67 SRS-alignment (plan Task 4, spec v1.2): widens payment.Refunds.status to
/// VARCHAR(30) and its CHECK constraint to accept the six new UC-42 values, and adds the six
/// nullable operator-confirmation / partner-return columns. Runs against a synthetic baseline
/// shaped like the table's pre-migration schema (old VARCHAR(20) status, old CHECK constraint),
/// including an existing 'Processed' row, to prove the script applies cleanly to an upgraded
/// database with real data (Q3) and not just an empty one.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class RefundUc42SchemaMigrationSqlServerTests
{
    private const string MigrationFileName = "20261120_refund_uc42_status_and_return_fields.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RunTwiceOnDatabaseWithExistingRefundRow_IsIdempotent()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await SeedExistingProcessedRefundAsync(database);

        await database.ExecuteScriptAsync(MigrationPath());
        Func<Task> rerun = () => database.ExecuteScriptAsync(MigrationPath());

        await rerun.Should().NotThrowAsync();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM payment.Refunds WHERE status = N'Processed';"))
            .Should().Be(1, "the migration must not touch existing data");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_StatusConstraintAcceptsAllSevenNewUc42ValuesAndTheExistingOnes()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());
        await SeedBookingAsync(database);

        string[] allAccepted =
        [
            "PendingOperatorConfirmation", "AwaitingPartnerReturn", "ReturnReported", "ReturnReceived",
            "Reconciliation", "Escalated", // new (UC-42)
            "Pending", "Processing", "RetryPending", "ManualReview", "Processed", "Failed", "Rejected", // pre-existing
        ];

        foreach (var status in allAccepted)
        {
            Func<Task> insert = () => database.ExecuteNonQueryAsync(RefundInsertSql(bookingId: 1, status: status));
            await insert.Should().NotThrowAsync($"'{status}' must be accepted by CK_Refunds_Status");
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_StatusConstraintRejectsAnUnknownValue()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());
        await SeedBookingAsync(database);

        Func<Task> insert = () => database.ExecuteNonQueryAsync(RefundInsertSql(bookingId: 1, status: "CarriedOver"));

        await insert.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WidensStatusColumnToThirtyCharacters()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Refunds') AND name = N'status'
              AND system_type_id = TYPE_ID(N'varchar') AND max_length = 30 AND is_nullable = 0;
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_AddsTheSixOperatorConfirmationColumnsAsNullable()
    {
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Refunds')
              AND name IN (N'operator_confirmed_at', N'return_deadline_at', N'return_reported_at', N'return_received_at')
              AND system_type_id = TYPE_ID(N'datetime2') AND is_nullable = 1;
            """)).Should().Be(4);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Refunds') AND name = N'operator_return_share'
              AND system_type_id = TYPE_ID(N'decimal') AND is_nullable = 1;
            """)).Should().Be(1);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Refunds') AND name = N'return_reference'
              AND system_type_id = TYPE_ID(N'nvarchar') AND max_length = 300 AND is_nullable = 1;
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_LeavesThePreexistingPostTripRefundColumnUntouched()
    {
        // The post-trip-refund feature (a sibling branch, already merged) added this column
        // separately — this migration must not drop or alter it.
        await using var database = await CreateBaseDatabaseAsync();
        await database.ExecuteScriptAsync(MigrationPath());

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Refunds') AND name = N'post_trip_refund_request_id';
            """)).Should().Be(1);
    }

    private static async Task<SqlServerTestDatabase> CreateBaseDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commerce;");
        // Shaped like the table's pre-migration schema: old VARCHAR(20) status with the old
        // CHECK constraint, plus the pre-existing post_trip_refund_request_id column from the
        // unrelated, already-merged post-trip-refund feature.
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.Bookings (
                booking_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Bookings PRIMARY KEY
            );
            CREATE TABLE payment.Refunds (
                refund_id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Refunds PRIMARY KEY,
                booking_id BIGINT NOT NULL,
                transaction_id BIGINT NULL,
                cancellation_operation_id BIGINT NULL,
                amount DECIMAL(12,2) NOT NULL CONSTRAINT CK_Refunds_amount CHECK (amount > 0),
                reason NVARCHAR(500) NULL,
                initiated_by VARCHAR(14) NOT NULL
                    CONSTRAINT CK_Refunds_InitiatedBy CHECK (initiated_by IN
                        (N'Traveler', N'Staff', N'Administrator', N'TourOperator', N'System')),
                status VARCHAR(20) NOT NULL
                    CONSTRAINT CK_Refunds_Status CHECK (status IN
                        (N'Rejected', N'Failed', N'Processed', N'ManualReview', N'RetryPending', N'Processing', N'Pending')),
                requested_at DATETIME2 NOT NULL,
                processed_at DATETIME2 NULL,
                gateway_reference NVARCHAR(150) NULL,
                attempt_count INT NOT NULL CONSTRAINT CK_Refunds_AttemptCount CHECK (attempt_count >= 0) CONSTRAINT DF_Refunds_attempt_count DEFAULT 0,
                last_attempt_at DATETIME2 NULL,
                next_attempt_at DATETIME2 NULL,
                last_error_code NVARCHAR(100) NULL,
                last_error_message NVARCHAR(1000) NULL,
                post_trip_refund_request_id BIGINT NULL
            );
            """);
        return database;
    }

    private static async Task SeedBookingAsync(SqlServerTestDatabase database) =>
        await database.ExecuteNonQueryAsync("SET IDENTITY_INSERT commerce.Bookings ON; INSERT commerce.Bookings (booking_id) VALUES (1); SET IDENTITY_INSERT commerce.Bookings OFF;");

    private static async Task SeedExistingProcessedRefundAsync(SqlServerTestDatabase database)
    {
        await SeedBookingAsync(database);
        await database.ExecuteNonQueryAsync(RefundInsertSql(bookingId: 1, status: "Processed"));
    }

    private static string RefundInsertSql(long bookingId, string status)
    {
        return $"""
            INSERT payment.Refunds (booking_id, amount, initiated_by, status, requested_at, attempt_count)
            VALUES ({bookingId}, 100000, N'System', N'{status}', SYSUTCDATETIME(), 0);
            """;
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
            throw new DirectoryNotFoundException("Could not locate the repository root for the refund migration.");
        }

        return Path.Combine(directory.FullName, "database", "migrations", MigrationFileName);
    }
}