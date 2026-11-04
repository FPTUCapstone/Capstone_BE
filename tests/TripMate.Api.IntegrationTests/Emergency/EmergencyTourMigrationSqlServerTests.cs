using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Emergency;

public sealed class EmergencyTourMigrationSqlServerTests
{
    private static readonly string[] Uc70MigrationFileNames =
    [
        "20261028_uc70_emergency_cancellation.sql",
        "20261102_weather_operator_review.sql",
    ];

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CanonicalSchemaV7_AloneAlreadyCarriesAllUc70Objects()
    {
        // Drift guard: tripmate_schema_v7.sql is the canonical full schema, so it must
        // carry every UC-70 object on its own, before any migration runs. If a future
        // change ships in migrations only (or removes it from v7), this goes red.
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        string schemaPath = Path.Combine(AppContext.BaseDirectory, "Database", "tripmate_schema_v7.sql");
        await database.ExecuteScriptAsync(schemaPath);

        await AssertCanonicalShapeAsync(database);
        await AssertConstraintsAndKeysTrustedAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromCurrentSchema_CreatesCanonicalUc70ObjectsAndCanRunAgain()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();

        await ApplyAllUc70MigrationsAsync(database);
        await AssertCanonicalShapeAsync(database);
        await AssertPostMigrationSessionReasonAsync(database);
        await AssertConstraintsAndKeysTrustedAsync(database);
        await AssertPaymentStatusCheckKeptAsync(database);

        await ApplyAllUc70MigrationsAsync(database);
        await AssertCanonicalShapeAsync(database);
        await AssertPostMigrationSessionReasonAsync(database);
        await AssertConstraintsAndKeysTrustedAsync(database);
        await AssertPaymentStatusCheckKeptAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_FromLegacySchema_PreservesExistingDataAndEnablesUc70Transitions()
    {
        await using var database = await CreateLegacyDatabaseAsync();

        // Verify pre-migration state
        (await database.ExecuteScalarAsync<string>("SELECT status FROM commerce.TourSchedules WHERE schedule_id = 1;"))
            .Should().Be("Scheduled");
        (await database.ExecuteScalarAsync<string>("SELECT status FROM commerce.Bookings WHERE booking_id = 1;"))
            .Should().Be("Confirmed");
        (await database.ExecuteScalarAsync<string>("SELECT status FROM payment.Refunds WHERE refund_id = 1;"))
            .Should().Be("Pending");

        // First run
        await ApplyAllUc70MigrationsAsync(database);
        await AssertCanonicalShapeAsync(database);
        await AssertConstraintsAndKeysTrustedAsync(database);

        // Verify existing rows are preserved unchanged
        (await database.ExecuteScalarAsync<string>("SELECT status FROM commerce.TourSchedules WHERE schedule_id = 1;"))
            .Should().Be("Scheduled");
        (await database.ExecuteScalarAsync<string>("SELECT status FROM commerce.Bookings WHERE booking_id = 1;"))
            .Should().Be("Confirmed");
        (await database.ExecuteScalarAsync<string>("SELECT status FROM payment.Refunds WHERE refund_id = 1;"))
            .Should().Be("Pending");
        (await database.ExecuteScalarAsync<decimal>("SELECT amount FROM payment.Refunds WHERE refund_id = 1;"))
            .Should().Be(50000m);

        // Verify new UC-70 statuses work on existing rows
        await database.ExecuteNonQueryAsync("""
            UPDATE commerce.TourSchedules SET status = 'EmergencyStopped' WHERE schedule_id = 1;
            UPDATE commerce.Bookings SET status = 'Interrupted' WHERE booking_id = 1;
            """);

        (await database.ExecuteScalarAsync<string>("SELECT status FROM commerce.TourSchedules WHERE schedule_id = 1;"))
            .Should().Be("EmergencyStopped");
        (await database.ExecuteScalarAsync<string>("SELECT status FROM commerce.Bookings WHERE booking_id = 1;"))
            .Should().Be("Interrupted");
        (await database.ExecuteScalarAsync<string>("SELECT status FROM payment.Refunds WHERE refund_id = 1;"))
            .Should().Be("Pending", "UC-70 leaves existing refunds alone");

        // Rerun must remain idempotent
        await ApplyAllUc70MigrationsAsync(database);
        await AssertCanonicalShapeAsync(database);
        await AssertConstraintsAndKeysTrustedAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenEmergencyOperationTableHasWrongColumnShape_RollsBackWithoutPartialUpgrade()
    {
        await using var database = await CreateLegacyDatabaseAsync();
        // Create an incompatible operation table with integer action instead of varchar(24)
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.EmergencyTourCancellationOperations (
                operation_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                action INT NOT NULL
            );
            """);

        Func<Task> action = () => ApplyMigrationAsync(database);

        var exception = await action.Should().ThrowAsync<SqlException>();
        exception.Which.Message.Should().Contain("UC-70 emergency operation table has an incompatible shape");

        // The transaction must roll back completely without partial upgrade
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.Bookings')
              AND name = N'emergency_notice_acknowledged_at';
            """)).Should().Be(0, "emergency_notice_acknowledged_at must not be added when migration fails");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'planning.Itineraries')
              AND name = N'source_booking_id';
            """)).Should().Be(0, "source_booking_id must not be added when migration fails");
    }

    private static async Task<SqlServerTestDatabase> CreateLegacyDatabaseAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteNonQueryAsync("CREATE SCHEMA commerce;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA payment;");
        await database.ExecuteNonQueryAsync("CREATE SCHEMA planning;");
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE dbo.Users (
                user_id BIGINT NOT NULL PRIMARY KEY
            );
            CREATE TABLE commerce.Tours (
                tour_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                operator_user_id BIGINT NOT NULL REFERENCES dbo.Users(user_id),
                title NVARCHAR(200) NOT NULL,
                base_price DECIMAL(12,2) NOT NULL,
                status VARCHAR(12) NOT NULL DEFAULT 'Approved'
            );
            CREATE TABLE commerce.TourSchedules (
                schedule_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                tour_id BIGINT NOT NULL REFERENCES commerce.Tours(tour_id),
                start_datetime DATETIME2 NOT NULL,
                end_datetime DATETIME2 NOT NULL,
                total_capacity INT NOT NULL,
                reserved_capacity INT NOT NULL DEFAULT 0,
                status VARCHAR(12) NOT NULL DEFAULT 'Scheduled'
                    CONSTRAINT CK_TourSchedules_Status CHECK (status IN ('Scheduled','Cancelled','Completed')),
                created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            CREATE TABLE commerce.Bookings (
                booking_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                traveler_user_id BIGINT NOT NULL REFERENCES dbo.Users(user_id),
                tour_schedule_id BIGINT NOT NULL REFERENCES commerce.TourSchedules(schedule_id),
                quantity INT NOT NULL,
                total_amount DECIMAL(12,2) NOT NULL,
                status VARCHAR(15) NOT NULL DEFAULT 'Confirmed'
                    CONSTRAINT CK_Bookings_Status CHECK (status IN ('PendingPayment','Confirmed','Cancelled','Completed','Expired')),
                created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            CREATE TABLE payment.PaymentTransactions (
                transaction_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                booking_id BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id),
                gateway VARCHAR(20) NOT NULL DEFAULT 'PayOS',
                amount DECIMAL(12,2) NOT NULL,
                order_code BIGINT NOT NULL DEFAULT 1,
                status VARCHAR(20) NOT NULL DEFAULT 'Success',
                created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            CREATE TABLE payment.Refunds (
                refund_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                booking_id BIGINT NOT NULL REFERENCES commerce.Bookings(booking_id),
                cancellation_operation_id BIGINT NULL,
                amount DECIMAL(12,2) NOT NULL CONSTRAINT CK_Refunds_Amount CHECK (amount > 0),
                initiated_by VARCHAR(14) NOT NULL,
                status VARCHAR(20) NOT NULL DEFAULT 'Pending'
                    CONSTRAINT CK_Refunds_Status CHECK (status IN ('Pending','Processing','RetryPending','ManualReview','Processed','Failed','Rejected')),
                requested_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            CREATE TABLE planning.Itineraries (
                itinerary_id BIGINT IDENTITY(1,1) PRIMARY KEY,
                traveler_user_id BIGINT NOT NULL REFERENCES dbo.Users(user_id),
                source_type VARCHAR(14) NOT NULL,
                status VARCHAR(10) NOT NULL DEFAULT 'Draft'
            );

            INSERT INTO dbo.Users (user_id) VALUES (1), (2);
            INSERT INTO commerce.Tours (operator_user_id, title, base_price) VALUES (1, N'Legacy Tour', 100000);
            INSERT INTO commerce.TourSchedules (tour_id, start_datetime, end_datetime, total_capacity, status)
                VALUES (1, '2026-11-01 08:00:00', '2026-11-01 17:00:00', 20, 'Scheduled');
            INSERT INTO commerce.Bookings (traveler_user_id, tour_schedule_id, quantity, total_amount, status)
                VALUES (2, 1, 2, 200000, 'Confirmed');
            INSERT INTO payment.PaymentTransactions (booking_id, amount) VALUES (1, 200000);
            INSERT INTO payment.Refunds (booking_id, amount, initiated_by, status)
                VALUES (1, 50000, 'TourOperator', 'Pending');
            INSERT INTO planning.Itineraries (traveler_user_id, source_type, status)
                VALUES (2, 'Manual', 'Draft');
            """);
        return database;
    }

    private static async Task ApplyAllUc70MigrationsAsync(SqlServerTestDatabase database)
    {
        foreach (var migrationFileName in Uc70MigrationFileNames)
        {
            await ApplyMigrationAsync(database, migrationFileName);
        }
    }

    private static Task ApplyMigrationAsync(
        SqlServerTestDatabase database,
        string migrationFileName = "20261028_uc70_emergency_cancellation.sql")
    {
        string migrationPath = Path.Combine(
            AppContext.BaseDirectory, "Database", "migrations", migrationFileName);
        return database.ExecuteScriptAsync(migrationPath);
    }

    private static async Task AssertCanonicalShapeAsync(SqlServerTestDatabase database)
    {
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.tables
            WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations');
            """)).Should().Be(1);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
              AND name IN (
                  N'evidence_url',
                  N'authority_reference',
                  N'evidence_provenance',
                  N'evidence_added_later');
            """)).Should().Be(4);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE (object_id = OBJECT_ID(N'planning.Itineraries')
                   AND name = N'source_booking_id')
               OR (object_id = OBJECT_ID(N'commerce.Bookings')
                   AND name = N'emergency_notice_acknowledged_at');
            """)).Should().Be(2);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
              AND name = N'emergency_contact'
              AND is_nullable = 1;
            """)).Should().Be(1, "emergency_contact is mandatory for an active stop only, so the column must be nullable");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE name = N'FK_Itineraries_SourceBooking';
            """)).Should().Be(1);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE (parent_object_id = OBJECT_ID(N'commerce.TourSchedules')
                   AND definition LIKE '%EmergencyStopped%')
               OR (parent_object_id = OBJECT_ID(N'commerce.Bookings')
                   AND definition LIKE '%Interrupted%');
            """)).Should().Be(2);

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'payment.RefundAllocations');
            """)).Should().Be(0, "UC-70 owns no refund table");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'payment.Refunds')
              AND name IN (N'emergency_cancellation_operation_id', N'approved_amount');
            """)).Should().Be(0, "UC-70 adds nothing to the refund table");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
              AND name IN (N'refund_case_count', N'automatic_refund_amount', N'refund_decision_due_at');
            """)).Should().Be(0, "a decision records no refund result");

        // Weather evidence is review-only and must not extend UC-70 cancellation ownership.
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
              AND name IN (
                  N'initiated_by_type',
                  N'trigger_type',
                  N'weather_provider',
                  N'weather_event_id',
                  N'weather_revision',
                  N'policy_version',
                  N'decision_reason',
                  N'evaluated_at');
            """)).Should().Be(0, "manual UC-70 operations must not carry automatic weather-trigger columns");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
              AND name = N'actor_user_id'
              AND is_nullable = 0;
            """)).Should().Be(1, "every UC-70 cancellation must identify its Operator actor");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.tables
            WHERE object_id IN (
                OBJECT_ID(N'trip.WeatherAlertInboxItems'),
                OBJECT_ID(N'trip.WeatherEventEvaluations'),
                OBJECT_ID(N'trip.WeatherSafetyReviews'),
                OBJECT_ID(N'trip.WeatherLocationMappings')
            );
            """)).Should().Be(4, "all weather ingestion and Operator-review tables must exist");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE object_id = OBJECT_ID(N'payment.RefundRequestedOutboxItems');
            """)).Should().Be(0, "weather review must not create a refund-request outbox");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'trip.WeatherEvents')
              AND name IN (
                  N'provider',
                  N'provider_event_id',
                  N'revision',
                  N'original_authority',
                  N'original_source_reference',
                  N'official_vietnam_risk_level',
                  N'verified_directive',
                  N'is_withdrawn',
                  N'geometry_geojson',
                  N'raw_payload_hash',
                  N'updated_at');
            """)).Should().Be(11, "WeatherEvents must carry all 11 UC-70 official authority columns");
    }

    private static async Task AssertPaymentStatusCheckKeptAsync(SqlServerTestDatabase database)
    {
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'commerce.Bookings')
              AND definition LIKE N'%payment_status%';
            """)).Should().Be(1, "UC-70 must not drop a CHECK it does not own");
    }

    private static async Task AssertPostMigrationSessionReasonAsync(SqlServerTestDatabase database)
    {
        // trip.TripSessions carries the pre-UC-13 shape in v7; the completion-reason widening
        // only exists after the UC-70 migration has run on top of the navigation migration.
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
              AND name = N'CK_TripSessions_CompletionReason'
              AND definition LIKE '%SafetyIncident%'
              AND definition NOT LIKE '%AllItemsReached%';
            """)).Should().Be(1, "UC-70 emergency stop reasons must be allowed on TripSessions");
    }

    private static async Task AssertConstraintsAndKeysTrustedAsync(SqlServerTestDatabase database)
    {
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id IN (
                OBJECT_ID(N'commerce.TourSchedules'),
                OBJECT_ID(N'commerce.Bookings'),
                OBJECT_ID(N'commerce.EmergencyTourCancellationOperations'),
                OBJECT_ID(N'trip.WeatherAlertInboxItems'),
                OBJECT_ID(N'trip.WeatherEvents'),
                OBJECT_ID(N'trip.WeatherSafetyReviews'),
                OBJECT_ID(N'trip.WeatherLocationMappings')
            )
            AND is_not_trusted = 1;
            """)).Should().Be(0, "all check constraints must be trusted");

        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id IN (
                OBJECT_ID(N'commerce.EmergencyTourCancellationOperations'),
                OBJECT_ID(N'planning.Itineraries'),
                OBJECT_ID(N'trip.WeatherEventEvaluations'),
                OBJECT_ID(N'trip.WeatherSafetyReviews')
            )
            AND is_not_trusted = 1;
            """)).Should().Be(0, "all foreign keys must be trusted");
    }
}
