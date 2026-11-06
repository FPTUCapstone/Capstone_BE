using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.Emergency;

/// <summary>
/// The UC-70 schema on real SQL Server: the canonical schema alone, an upgrade of the
/// origin/develop schema with data, reruns as apply-schema.sh performs them, a rollback on a
/// wrong shape, and parity between the canonical schema and the migrated one.
/// </summary>
public sealed class EmergencyTourMigrationSqlServerTests
{
    private static readonly string[] Uc70Tables =
    [
        "commerce.EmergencyTourCancellationOperations",
        "trip.WeatherAlertInboxItems",
        "trip.WeatherSafetyReviews",
        "trip.WeatherLocationMappings",
    ];

    // Columns UC-70 adds to tables it does not own.
    private static readonly (string Table, string Column)[] Uc70Columns =
    [
        ("commerce.TourSchedules", "meeting_latitude"),
        ("commerce.TourSchedules", "meeting_longitude"),
        ("commerce.TourSchedules", "status"),
        ("commerce.TourItineraryItems", "is_required_for_weather"),
        ("commerce.Bookings", "status"),
        ("commerce.Bookings", "emergency_notice_acknowledged_at"),
        ("planning.Itineraries", "source_booking_id"),
        ("trip.WeatherEvents", "provider"),
        ("trip.WeatherEvents", "provider_event_id"),
        ("trip.WeatherEvents", "original_authority"),
        ("trip.WeatherEvents", "official_vietnam_risk_level"),
        ("trip.WeatherEvents", "verified_directive"),
        ("trip.WeatherEvents", "is_withdrawn"),
        ("trip.WeatherEvents", "updated_at"),
    ];

    private static readonly string[] Uc70Constraints =
    [
        "CK_TourSchedules_Status", "CK_TourSchedules_WeatherMeetingCoordinates", "CK_Bookings_Status",
        "CK_WeatherEvents_RiskLevel",
        "CK_EmergencyTourCancellationOperations_Counts", "FK_EmergencyTourCancellationOperations_Schedule",
        "FK_EmergencyTourCancellationOperations_Actor", "FK_Itineraries_SourceBooking",
        "CK_WeatherAlertInboxItems_Status", "CK_WeatherAlertInboxItems_Attempts",
        "FK_WeatherSafetyReviews_TourSchedule", "FK_WeatherSafetyReviews_Operator", "CK_WeatherSafetyReviews_Status",
        "CK_WeatherLocationMappings_Coordinates",
    ];

    private static readonly string[] Uc70Indexes =
    [
        "UX_EmergencyTourCancellationOperations_Schedule", "UX_EmergencyTourCancellationOperations_ActorCommand",
        "IX_Itineraries_SourceBookingId", "UX_WeatherEvents_ProviderEvent",
        "UX_WeatherAlertInboxItems_ProviderNotification", "IX_WeatherAlertInboxItems_StatusLease",
        "UX_WeatherSafetyReviews_ScheduleEvent", "IX_WeatherSafetyReviews_StatusDeadline",
        "UX_WeatherLocationMappings_Key", "UX_WeatherLocationMappings_TomorrowId",
    ];

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CanonicalSchemaV7_AloneAlreadyCarriesAllUc70Objects()
    {
        // Drift guard: tripmate_schema_v7.sql must build the final UC-70 shape before any migration.
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteScriptAsync(SchemaPath("tripmate_schema_v7.sql"));
        await database.ExecuteNonQueryAsync(SeedSql);

        await AssertFinalShapeAsync(database);
        await AssertMeetingCoordinatesAreEnforcedAsync(database);

        // EF reads every UC-70 column of these tables with its mapped type and nullability.
        await using var db = database.CreateDbContext();
        (await db.TourSchedules.Select(s => new { s.Id, s.MeetingLatitude, s.MeetingLongitude, s.Status }).ToListAsync())
            .Should().NotBeEmpty();
        await db.TourItineraryItems.Select(i => new { i.Id, i.IsRequiredForWeather }).ToListAsync();
        await db.WeatherAlertInboxItems.ToListAsync();
        await db.WeatherSafetyReviews.ToListAsync();
        await db.WeatherLocationMappings.ToListAsync();
        await db.EmergencyTourCancellationOperations.ToListAsync();
        await db.WeatherEvents.Select(e => new { e.Id, e.Provider, e.ProviderEventId, e.OriginalAuthority,
            e.OfficialVietnamRiskLevel, e.VerifiedDirective, e.IsWithdrawn, e.UpdatedAtUtc }).ToListAsync();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migrations_FromTheDevelopSchemaWithData_UpgradeKeepTheDataAndCanRunAgain()
    {
        await using var database = await CreateDevelopDatabaseWithDataAsync();

        await ApplyAllMigrationsAsync(database);
        await AssertFinalShapeAsync(database);
        await AssertMeetingCoordinatesAreEnforcedAsync(database);
        await AssertDevelopDataUnchangedAsync(database);
        await AssertMigratedSessionReasonsAsync(database);

        // UC-70 values are accepted on existing rows.
        await database.ExecuteNonQueryAsync("""
            UPDATE commerce.TourSchedules SET status = 'EmergencyStopped' WHERE schedule_id = 1;
            UPDATE commerce.Bookings SET status = 'Interrupted', emergency_notice_acknowledged_at = SYSUTCDATETIME()
                WHERE booking_code = 'UC70-UPGRADE-1';
            """);

        // apply-schema.sh reruns every migration on each deploy.
        await ApplyAllMigrationsAsync(database);
        await AssertFinalShapeAsync(database);
        (await database.ExecuteScalarAsync<string>("SELECT status FROM commerce.Bookings WHERE booking_code = 'UC70-UPGRADE-1';"))
            .Should().Be("Interrupted");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CanonicalAndMigratedSchemas_HaveTheSameUc70Shape()
    {
        await using var canonical = await SqlServerTestDatabase.CreateEmptyAsync();
        await canonical.ExecuteScriptAsync(SchemaPath("tripmate_schema_v7.sql"));
        await using var migrated = await CreateDevelopDatabaseWithDataAsync();
        await ApplyAllMigrationsAsync(migrated);

        foreach (string query in ParityQueries())
        {
            (await ReadRowsAsync(migrated, query)).Should().Equal(await ReadRowsAsync(canonical, query), query);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migrations_RerunAfterAnEmergencyStop_DoNotRejectTheStoredReason()
    {
        // A Stop stores an emergency reason on the traveler's navigation session; the next
        // deploy reruns the UC-13 and UC-21 scripts, whose data guards must recognise it.
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            INSERT INTO dbo.Users (role, full_name, email) VALUES ('Traveler', N'UC70 Traveler', 'uc70-rerun@example.test');
            DECLARE @user BIGINT = SCOPE_IDENTITY();
            INSERT INTO planning.Itineraries (traveler_user_id, source_type) VALUES (@user, 'Manual');
            DECLARE @itinerary BIGINT = SCOPE_IDENTITY();
            INSERT INTO trip.TripSessions (itinerary_id, traveler_user_id, requested_itinerary_id, start_idempotency_key,
                fsm_state, completion_reason, ended_at)
            VALUES (@itinerary, @user, @itinerary, NEWID(), 'Interrupted', 'WeatherEmergency', SYSUTCDATETIME()),
                   (@itinerary, @user, @itinerary, NEWID(), 'Interrupted', 'SafetyIncident', SYSUTCDATETIME());
            """);

        Func<Task> rerun = () => ApplyAllMigrationsAsync(database);

        await rerun.Should().NotThrowAsync();
        await AssertFinalShapeAsync(database);
        await AssertMigratedSessionReasonsAsync(database);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_WhenTheDecisionTableHasAWrongShape_RollsBackEverything()
    {
        await using var database = await CreateDevelopDatabaseWithDataAsync();
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE commerce.EmergencyTourCancellationOperations (
                operation_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                action INT NOT NULL
            );
            """);

        Func<Task> act = () => database.ExecuteScriptAsync(MigrationPath("20261028_uc70_emergency_cancellation.sql"));

        (await act.Should().ThrowAsync<SqlException>()).Which.Message
            .Should().Contain("UC-70 emergency operation table has an incompatible shape");
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE (object_id = OBJECT_ID(N'commerce.Bookings') AND name = N'emergency_notice_acknowledged_at')
               OR (object_id = OBJECT_ID(N'planning.Itineraries') AND name = N'source_booking_id');
            """)).Should().Be(0, "nothing of a failed run may remain");
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE (parent_object_id = OBJECT_ID(N'commerce.TourSchedules') AND definition LIKE '%EmergencyStopped%')
               OR (parent_object_id = OBJECT_ID(N'commerce.Bookings') AND definition LIKE '%Interrupted%');
            """)).Should().Be(0);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task WeatherMigration_WhenAWeatherTableCarriesARemovedColumn_RollsBack()
    {
        await using var database = await CreateDevelopDatabaseWithDataAsync();
        await database.ExecuteScriptAsync(MigrationPath("20261028_uc70_emergency_cancellation.sql"));
        await database.ExecuteNonQueryAsync("""
            CREATE TABLE trip.WeatherLocationMappings (
                location_mapping_id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                trip_mate_location_key VARCHAR(100) NOT NULL,
                tomorrow_location_id VARCHAR(100) NOT NULL,
                latitude DECIMAL(9,6) NOT NULL,
                longitude DECIMAL(9,6) NOT NULL,
                name NVARCHAR(200) NULL,
                linked_departures_count INT NOT NULL,
                last_departure_end_at DATETIME2 NULL,
                is_active BIT NOT NULL,
                created_at DATETIME2 NOT NULL,
                updated_at DATETIME2 NOT NULL
            );
            """);

        Func<Task> act = () => database.ExecuteScriptAsync(MigrationPath("20261102_weather_operator_review.sql"));

        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("UC-70 weather tables have unexpected columns");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'trip.WeatherAlertInboxItems');"))
            .Should().Be(0, "nothing of a failed run may remain");
    }

    // trip.TripSessions.completion_reason comes from the UC-13 migration, not the canonical schema,
    // so the widened reason list is checked on migrated databases only.
    private static async Task AssertMigratedSessionReasonsAsync(SqlServerTestDatabase database)
    {
        string[] reasons = ["RouteFinished", "TravelerStopped", "Expired", "GroupAccessRevoked", "WeatherEmergency",
            "AuthorityDirective", "DestinationUnavailable", "TransportFailure", "SafetyIncident", "Other"];
        (await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions') AND name = N'CK_TripSessions_CompletionReason'
              AND is_disabled = 0 AND is_not_trusted = 0
              {string.Concat(reasons.Select(reason => $" AND definition LIKE N'%''{reason}''%'"))};
            """)).Should().Be(1, "every UC-13, UC-21 and UC-70 completion reason is allowed and trusted");
        (await database.ExecuteScalarAsync<int>(
            "SELECT CAST(COL_LENGTH(N'trip.TripSessions', N'completion_reason') AS INT);")).Should().Be(24);
    }

    private static async Task AssertFinalShapeAsync(SqlServerTestDatabase database)
    {
        // Exact column contract of every UC-70 column.
        (await ReadRowsAsync(database, $"""
            SELECT OBJECT_SCHEMA_NAME(c.object_id) + '.' + OBJECT_NAME(c.object_id), c.name, t.name,
                c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE {Uc70ColumnFilter("c")}
            ORDER BY 1, 2;
            """)).Should().Equal(
            "commerce.Bookings|emergency_notice_acknowledged_at|datetime2|8|27|7|True",
            "commerce.Bookings|status|varchar|14|0|0|False",
            "commerce.EmergencyTourCancellationOperations|action|varchar|24|0|0|False",
            "commerce.EmergencyTourCancellationOperations|actor_user_id|bigint|8|19|0|False",
            "commerce.EmergencyTourCancellationOperations|affected_booking_count|int|4|10|0|False",
            "commerce.EmergencyTourCancellationOperations|authority_reference|nvarchar|400|0|0|True",
            "commerce.EmergencyTourCancellationOperations|command_id|uniqueidentifier|16|0|0|False",
            "commerce.EmergencyTourCancellationOperations|committed_at|datetime2|8|27|7|False",
            "commerce.EmergencyTourCancellationOperations|emergency_contact|nvarchar|200|0|0|True",
            "commerce.EmergencyTourCancellationOperations|evidence_added_later|bit|1|1|0|False",
            "commerce.EmergencyTourCancellationOperations|evidence_provenance|varchar|20|0|0|False",
            "commerce.EmergencyTourCancellationOperations|evidence_reference|nvarchar|1000|0|0|True",
            "commerce.EmergencyTourCancellationOperations|evidence_url|nvarchar|2000|0|0|True",
            "commerce.EmergencyTourCancellationOperations|immediate_instructions|nvarchar|2000|0|0|True",
            "commerce.EmergencyTourCancellationOperations|operation_id|bigint|8|19|0|False",
            "commerce.EmergencyTourCancellationOperations|reason_category|varchar|40|0|0|False",
            "commerce.EmergencyTourCancellationOperations|reason_details|nvarchar|2000|0|0|False",
            "commerce.EmergencyTourCancellationOperations|request_hash|char|64|0|0|False",
            "commerce.EmergencyTourCancellationOperations|tour_schedule_id|bigint|8|19|0|False",
            "commerce.TourItineraryItems|is_required_for_weather|bit|1|1|0|True",
            "commerce.TourSchedules|meeting_latitude|decimal|5|9|6|True",
            "commerce.TourSchedules|meeting_longitude|decimal|5|9|6|True",
            "commerce.TourSchedules|status|varchar|20|0|0|False",
            "planning.Itineraries|source_booking_id|bigint|8|19|0|True",
            "trip.WeatherAlertInboxItems|attempt_count|int|4|10|0|False",
            "trip.WeatherAlertInboxItems|inbox_id|bigint|8|19|0|False",
            "trip.WeatherAlertInboxItems|last_error|nvarchar|2000|0|0|True",
            "trip.WeatherAlertInboxItems|lease_expires_at|datetime2|8|27|7|True",
            "trip.WeatherAlertInboxItems|leased_by|varchar|100|0|0|True",
            "trip.WeatherAlertInboxItems|max_attempts|int|4|10|0|False",
            "trip.WeatherAlertInboxItems|payload|nvarchar|-1|0|0|False",
            "trip.WeatherAlertInboxItems|processed_at|datetime2|8|27|7|True",
            "trip.WeatherAlertInboxItems|provider|varchar|50|0|0|False",
            "trip.WeatherAlertInboxItems|provider_notification_id|varchar|150|0|0|True",
            "trip.WeatherAlertInboxItems|received_at|datetime2|8|27|7|False",
            "trip.WeatherAlertInboxItems|status|varchar|30|0|0|False",
            "trip.WeatherEvents|is_withdrawn|bit|1|1|0|False",
            "trip.WeatherEvents|official_vietnam_risk_level|int|4|10|0|True",
            "trip.WeatherEvents|original_authority|nvarchar|400|0|0|True",
            "trip.WeatherEvents|provider|varchar|50|0|0|True",
            "trip.WeatherEvents|provider_event_id|varchar|150|0|0|True",
            "trip.WeatherEvents|updated_at|datetime2|8|27|7|True",
            "trip.WeatherEvents|verified_directive|varchar|50|0|0|True",
            "trip.WeatherLocationMappings|created_at|datetime2|8|27|7|False",
            "trip.WeatherLocationMappings|is_active|bit|1|1|0|False",
            "trip.WeatherLocationMappings|last_departure_end_at|datetime2|8|27|7|True",
            "trip.WeatherLocationMappings|latitude|decimal|5|9|6|False",
            "trip.WeatherLocationMappings|location_mapping_id|bigint|8|19|0|False",
            "trip.WeatherLocationMappings|longitude|decimal|5|9|6|False",
            "trip.WeatherLocationMappings|name|nvarchar|400|0|0|True",
            "trip.WeatherLocationMappings|tomorrow_location_id|varchar|100|0|0|False",
            "trip.WeatherLocationMappings|trip_mate_location_key|varchar|100|0|0|False",
            "trip.WeatherLocationMappings|updated_at|datetime2|8|27|7|False",
            "trip.WeatherSafetyReviews|acknowledged_at|datetime2|8|27|7|True",
            "trip.WeatherSafetyReviews|acknowledged_by_operator_id|bigint|8|19|0|True",
            "trip.WeatherSafetyReviews|acknowledgement_deadline|datetime2|8|27|7|False",
            "trip.WeatherSafetyReviews|created_at|datetime2|8|27|7|False",
            "trip.WeatherSafetyReviews|escalated_at|datetime2|8|27|7|True",
            "trip.WeatherSafetyReviews|escalation_contact|nvarchar|400|0|0|True",
            "trip.WeatherSafetyReviews|immediate_instructions|nvarchar|2000|0|0|True",
            "trip.WeatherSafetyReviews|provider|varchar|50|0|0|False",
            "trip.WeatherSafetyReviews|provider_event_id|varchar|150|0|0|False",
            "trip.WeatherSafetyReviews|review_id|bigint|8|19|0|False",
            "trip.WeatherSafetyReviews|status|varchar|30|0|0|False",
            "trip.WeatherSafetyReviews|tour_schedule_id|bigint|8|19|0|False",
            "trip.WeatherSafetyReviews|urgent_notice_sent_at|datetime2|8|27|7|False");

        // Every UC-70 constraint exists, is enabled and is trusted.
        (await ReadRowsAsync(database, $"""
            SELECT name, CAST(is_disabled AS INT) + CAST(is_not_trusted AS INT)
            FROM sys.check_constraints WHERE name IN ({Quoted(Uc70Constraints)})
            UNION ALL
            SELECT name, CAST(is_disabled AS INT) + CAST(is_not_trusted AS INT)
            FROM sys.foreign_keys WHERE name IN ({Quoted(Uc70Constraints)})
            ORDER BY 1;
            """)).Should().Equal(Uc70Constraints.Order(StringComparer.Ordinal).Select(name => $"{name}|0"));

        (await ReadRowsAsync(database, $"SELECT name FROM sys.indexes WHERE name IN ({Quoted(Uc70Indexes)}) ORDER BY 1;"))
            .Should().Equal(Uc70Indexes.Order(StringComparer.Ordinal));

        (await ReadRowsAsync(database, """
            SELECT c.name FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = OBJECT_ID(N'trip.WeatherSafetyReviews')
              AND ic.index_id = INDEXPROPERTY(ic.object_id, 'IX_WeatherSafetyReviews_StatusDeadline', 'IndexID')
            ORDER BY ic.key_ordinal;
            """)).Should().Equal(["status", "acknowledgement_deadline"], "the escalation sweep seeks on status then the deadline");

        // Removed persistence is gone everywhere.
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'trip.WeatherEventEvaluations');
            """)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE (object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations') AND name = N'contract_policy_snapshot')
               OR (object_id = OBJECT_ID(N'trip.WeatherAlertInboxItems') AND name = N'signature')
               OR (object_id = OBJECT_ID(N'trip.WeatherLocationMappings') AND name = N'linked_departures_count')
               OR (object_id = OBJECT_ID(N'trip.WeatherEvents')
                   AND name IN (N'geometry_geojson', N'raw_payload_hash', N'revision', N'original_source_reference'))
               OR (object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
                   AND name IN (N'initiated_by_type', N'trigger_type', N'weather_provider', N'weather_event_id',
                                N'weather_revision', N'policy_version', N'decision_reason', N'evaluated_at'));
            """)).Should().Be(0);

        // UC-70 owns no money schema.
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.tables
            WHERE object_id IN (OBJECT_ID(N'payment.RefundAllocations'), OBJECT_ID(N'payment.RefundRequestedOutboxItems'));
            """)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns
            WHERE (object_id = OBJECT_ID(N'payment.Refunds') AND name IN (N'emergency_cancellation_operation_id', N'approved_amount'))
               OR (object_id = OBJECT_ID(N'commerce.EmergencyTourCancellationOperations')
                   AND name IN (N'refund_case_count', N'automatic_refund_amount', N'refund_decision_due_at'));
            """)).Should().Be(0);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'commerce.Bookings') AND definition LIKE N'%payment_status%';
            """)).Should().Be(1, "UC-70 must not drop a CHECK it does not own");
    }

    private static async Task AssertMeetingCoordinatesAreEnforcedAsync(SqlServerTestDatabase database)
    {
        long scheduleId = await database.ExecuteScalarAsync<long>("SELECT MIN(schedule_id) FROM commerce.TourSchedules;");
        foreach (string coordinates in new[]
                 {
                     "meeting_latitude = 16.05, meeting_longitude = NULL",
                     "meeting_latitude = NULL, meeting_longitude = 108.2",
                     "meeting_latitude = 90.5, meeting_longitude = 108.2",
                     "meeting_latitude = 16.05, meeting_longitude = 180.5",
                 })
        {
            Func<Task> act = () => database.ExecuteNonQueryAsync(
                $"UPDATE commerce.TourSchedules SET {coordinates} WHERE schedule_id = {scheduleId};");
            (await act.Should().ThrowAsync<SqlException>(coordinates)).Which.Message
                .Should().Contain("CK_TourSchedules_WeatherMeetingCoordinates");
        }

        await database.ExecuteNonQueryAsync($"""
            UPDATE commerce.TourSchedules SET meeting_latitude = -90, meeting_longitude = 180 WHERE schedule_id = {scheduleId};
            UPDATE commerce.TourSchedules SET meeting_latitude = NULL, meeting_longitude = NULL WHERE schedule_id = {scheduleId};
            """);
    }

    private static async Task AssertDevelopDataUnchangedAsync(SqlServerTestDatabase database)
    {
        (await ReadRowsAsync(database, """
            SELECT s.status, b.status, b.payment_status, b.total_amount, i.source_type
            FROM commerce.TourSchedules s
            JOIN commerce.Bookings b ON b.tour_schedule_id = s.schedule_id
            JOIN planning.Itineraries i ON i.traveler_user_id = b.traveler_user_id;
            """)).Should().Equal("Scheduled|Confirmed|Paid|2000000.00|Manual");
        (await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM trip.WeatherEvents WHERE source = 'WeatherAPI' AND provider IS NULL AND is_withdrawn = 0;"))
            .Should().Be(1, "UC-59 weather rows keep their data and get the new defaults");
    }

    private static async Task<SqlServerTestDatabase> CreateDevelopDatabaseWithDataAsync()
    {
        var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await database.ExecuteScriptAsync(Path.Combine(
            AppContext.BaseDirectory, "Database", "Fixtures", "uc70_pre_migration_develop_schema_v7.sql"));
        await database.ExecuteNonQueryAsync(SeedSql);
        return database;
    }

    // Rows every UC-70 change touches, written against the columns both schemas share.
    private const string SeedSql = """
            INSERT INTO dbo.Users (role, full_name, email) VALUES ('TourOperator', N'UC70 Operator', 'uc70-op@example.test');
            DECLARE @operator BIGINT = SCOPE_IDENTITY();
            INSERT INTO dbo.Users (role, full_name, email) VALUES ('Traveler', N'UC70 Traveler', 'uc70-tr@example.test');
            DECLARE @traveler BIGINT = SCOPE_IDENTITY();
            INSERT INTO dbo.OperatorProfiles (user_id, company_name, tax_code, business_license_no, approval_status)
                VALUES (@operator, N'UC70 Tours', N'UC70-TAX', N'UC70-LIC', 'Approved');
            INSERT INTO commerce.Tours (operator_user_id, title, base_price) VALUES (@operator, N'Da Nang', 1000000);
            DECLARE @tour BIGINT = SCOPE_IDENTITY();
            INSERT INTO commerce.TourSchedules (tour_id, start_datetime, end_datetime, total_capacity)
                VALUES (@tour, '2026-12-01T08:00:00', '2026-12-01T17:00:00', 20);
            DECLARE @schedule BIGINT = SCOPE_IDENTITY();
            INSERT INTO commerce.Bookings (booking_code, traveler_user_id, tour_schedule_id, quantity, unit_price,
                total_amount, status, payment_status)
                VALUES ('UC70-UPGRADE-1', @traveler, @schedule, 2, 1000000, 2000000, 'Confirmed', 'Paid');
            INSERT INTO planning.Itineraries (traveler_user_id, source_type) VALUES (@traveler, 'Manual');
            INSERT INTO trip.WeatherEvents (event_type, severity, valid_from) VALUES ('Rain', 'Moderate', '2026-11-01T00:00:00');
        """;

    private static async Task ApplyAllMigrationsAsync(SqlServerTestDatabase database)
    {
        // The same order apply-schema.sh and the test database builder use.
        foreach (string migration in Directory
                     .EnumerateFiles(MigrationsDirectory(), "*.sql")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            await database.ExecuteScriptAsync(migration);
        }
    }

    private static IEnumerable<string> ParityQueries()
    {
        yield return $"""
            SELECT OBJECT_SCHEMA_NAME(c.object_id) + '.' + OBJECT_NAME(c.object_id), c.name, t.name, c.max_length,
                c.precision, c.scale, c.is_nullable, c.is_identity, ISNULL(d.definition, '')
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints d ON d.object_id = c.default_object_id
            WHERE {Uc70ColumnFilter("c")} ORDER BY 1, 2;
            """;
        yield return $"""
            SELECT name, definition FROM sys.check_constraints WHERE name IN ({Quoted(Uc70Constraints)})
            UNION ALL
            SELECT d.name, d.definition FROM sys.default_constraints d
            WHERE d.parent_object_id IN ({string.Join(", ", Uc70Tables.Select(table => $"OBJECT_ID(N'{table}')"))})
            ORDER BY 1;
            """;
        yield return $"""
            SELECT fk.name, OBJECT_NAME(fk.referenced_object_id), COL_NAME(fkc.parent_object_id, fkc.parent_column_id),
                COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id), fk.delete_referential_action
            FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            WHERE fk.name IN ({Quoted(Uc70Constraints)}) ORDER BY 1, 3;
            """;
        yield return $"""
            SELECT i.name, i.is_unique, i.is_unique_constraint, ISNULL(i.filter_definition, ''), ic.key_ordinal,
                COL_NAME(ic.object_id, ic.column_id)
            FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            WHERE i.name IN ({Quoted(Uc70Indexes)}) OR (i.is_primary_key = 1
                AND i.object_id IN ({string.Join(", ", Uc70Tables.Select(table => $"OBJECT_ID(N'{table}')"))}))
            ORDER BY 1, 5;
            """;
    }

    private static string Uc70ColumnFilter(string alias) =>
        $"({alias}.object_id IN ({string.Join(", ", Uc70Tables.Select(table => $"OBJECT_ID(N'{table}')"))})"
        + string.Concat(Uc70Columns.Select(column =>
            $" OR ({alias}.object_id = OBJECT_ID(N'{column.Table}') AND {alias}.name = N'{column.Column}')"))
        + ")";

    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(name => $"N'{name}'"));

    private static async Task<List<string>> ReadRowsAsync(SqlServerTestDatabase database, string query)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount)
                .Select(index => Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    private static string SchemaPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Database", fileName);

    private static string MigrationPath(string fileName) => Path.Combine(MigrationsDirectory(), fileName);

    // Every migration in the repository, as apply-schema.sh sees them (the test output links only some).
    private static string MigrationsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "database", "migrations");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("database/migrations was not found above the test output.");
    }
}
