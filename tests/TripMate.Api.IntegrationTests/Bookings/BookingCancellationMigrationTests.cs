using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Bookings;

public sealed class BookingCancellationMigrationTests
{
    private const string MigrationFileName = "20261015_add_booking_cancellation.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromLegacySchema_CreatesCanonicalObjectsAndCanRunAgain()
    {
        await using var database = await CreateLegacyDatabaseAsync();

        await ApplyMigrationAsync(database);
        await AssertCanonicalShapeAsync(database);
        await ApplyMigrationAsync(database);
        await AssertCanonicalShapeAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenSameNamedOperationTableHasWrongColumnShape_RollsBack()
    {
        await using var database = await CreateLegacyDatabaseAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.BookingCancellationOperations (
                operation_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                booking_id BIGINT NOT NULL,
                actor_user_id BIGINT NOT NULL,
                actor NVARCHAR(12) NOT NULL,
                idempotency_key UNIQUEIDENTIFIER NOT NULL,
                request_hash CHAR(64) NOT NULL,
                refund_amount DECIMAL(12,2) NOT NULL,
                cancelled_at DATETIME2 NOT NULL
            );
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        var exception = await action.Should().ThrowAsync<SqlException>();
        exception.Which.Message.Should().Contain("cancellation operation table has an incompatible shape");
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.Bookings')
              AND name = N'cancellation_policy_snapshot';
            """)).Should().Be(0, "the transaction must not partially upgrade the booking table");
    }

    private static async Task<SqlServerTestDatabase> CreateLegacyDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE SCHEMA commerce;
            CREATE SCHEMA payment;

            CREATE TABLE dbo.Users (
                user_id BIGINT NOT NULL PRIMARY KEY
            );
            CREATE TABLE commerce.Bookings (
                booking_id BIGINT NOT NULL PRIMARY KEY
            );
            CREATE TABLE payment.Refunds (
                refund_id BIGINT NOT NULL PRIMARY KEY
            );

            INSERT INTO dbo.Users (user_id) VALUES (1);
            INSERT INTO commerce.Bookings (booking_id) VALUES (100);
            """);
        return database;
    }

    private static Task ApplyMigrationAsync(SqlServerTestDatabase database)
    {
        var migrationPath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "migrations",
            MigrationFileName);
        return database.ExecuteScriptAsync(migrationPath);
    }

    private static async Task AssertCanonicalShapeAsync(SqlServerTestDatabase database)
    {
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns AS column_info
            INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
            WHERE column_info.object_id = OBJECT_ID(N'commerce.Bookings')
              AND column_info.name = N'cancellation_policy_snapshot'
              AND type_info.name = N'nvarchar'
              AND column_info.max_length = 1000
              AND column_info.is_nullable = 0;
            """)).Should().Be(1);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
              AND name IN (
                  N'FK_BookingCancellationOperations_Booking',
                  N'FK_BookingCancellationOperations_ActorUser');
            """)).Should().Be(2);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'commerce.BookingCancellationOperations')
              AND name IN (
                  N'CK_BookingCancellationOperations_Actor',
                  N'CK_BookingCancellationOperations_RefundAmount');
            """)).Should().Be(2);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'payment.Refunds')
              AND name = N'FK_Refunds_CancellationOperation';
            """)).Should().Be(1);
    }
}