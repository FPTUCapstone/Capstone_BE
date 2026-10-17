using FluentAssertions;

using Microsoft.Data.SqlClient;

using TripMate.Api.IntegrationTests.Infrastructure;

namespace TripMate.Api.IntegrationTests.ActiveTrips;

/// <summary>
/// UC-58/59 privacy remediation (migration
/// <c>20261011_add_trip_location_disclosure_consent.sql</c>). Proves the upgrade path a
/// production database actually takes: a pre-migration schema with existing trip/location data,
/// migrated in place, run twice, with legacy rows preserved and left undisclosable rather than
/// silently backfilled. Fresh-schema shape alone (asserted elsewhere) is not evidence this works
/// against an upgraded database — see TEAM_ENGINEERING_RULES D05/C18.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class TripLocationDisclosureConsentMigrationSqlServerTests
{
    private const string MigrationFileName = "20261011_add_trip_location_disclosure_consent.sql";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LegacySchema_MigrationAddsColumnsAndConsentTableWithoutTouchingExistingRows()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await RevertToPreUc58SchemaAndSeedLegacyDataAsync(database);

        await database.ExecuteScriptAsync(GetMigrationPath());
        await database.ExecuteScriptAsync(GetMigrationPath()); // repeatability: must not fail or duplicate

        // Legacy TripSessions row is untouched: same coordinates, and the new subject column is
        // NULL, not auto-backfilled (the migration's own comment: "no attributable subject and
        // remain undisclosable").
        var session = await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM trip.TripSessions
            WHERE session_id = {seed.SessionId}
              AND current_latitude = 16.061200
              AND current_longitude = 108.227700
              AND current_location_subject_user_id IS NULL;
            """);
        session.Should().Be(1);

        // Legacy TripLocationLogs rows are untouched in count and coordinates; subject stays NULL.
        var legacyLogRowCount = await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM trip.TripLocationLogs
            WHERE session_id = {seed.SessionId} AND subject_user_id IS NULL;
            """);
        legacyLogRowCount.Should().Be(seed.LegacyLogCount);

        // New columns exist with the canonical nullable BIGINT shape.
        var newColumnCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns AS column_info
            INNER JOIN sys.types AS type_info ON type_info.user_type_id = column_info.user_type_id
            WHERE (
                    (column_info.object_id = OBJECT_ID(N'trip.TripLocationLogs')
                        AND column_info.name = N'subject_user_id')
                 OR (column_info.object_id = OBJECT_ID(N'trip.TripSessions')
                        AND column_info.name = N'current_location_subject_user_id')
                  )
              AND type_info.name = N'bigint'
              AND column_info.is_nullable = 1;
            """);
        newColumnCount.Should().Be(2);

        // Both foreign keys exist and reference dbo.Users.
        var foreignKeyCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys AS fk
            WHERE fk.name IN (N'FK_TripLocationLogs_SubjectUser', N'FK_TripSessions_CurrentLocationSubjectUser')
              AND fk.referenced_object_id = OBJECT_ID(N'dbo.Users');
            """);
        foreignKeyCount.Should().Be(2);

        // The lookup index for the trail query exists with the right key order.
        var indexKeyColumns = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.index_columns AS ic
            INNER JOIN sys.indexes AS idx
                ON idx.object_id = ic.object_id AND idx.index_id = ic.index_id
            INNER JOIN sys.columns AS col
                ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE idx.name = N'IX_TripLocationLogs_SessionSubjectRecorded'
              AND idx.object_id = OBJECT_ID(N'trip.TripLocationLogs')
              AND (
                    (ic.key_ordinal = 1 AND col.name = N'session_id')
                 OR (ic.key_ordinal = 2 AND col.name = N'subject_user_id')
                 OR (ic.key_ordinal = 3 AND col.name = N'recorded_at')
                  );
            """);
        indexKeyColumns.Should().Be(3);

        // The consent table exists with its check constraints and both indexes.
        var consentTableColumnCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'trip.TripLocationDisclosureConsents')
              AND name IN (N'consent_id', N'session_id', N'user_id', N'audience', N'granted_at', N'revoked_at');
            """);
        consentTableColumnCount.Should().Be(6);

        var consentCheckConstraintCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'trip.TripLocationDisclosureConsents')
              AND name IN (N'CK_TripLocationDisclosureConsents_Audience', N'CK_TripLocationDisclosureConsents_Interval');
            """);
        consentCheckConstraintCount.Should().Be(2);

        var consentIndexCount = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'trip.TripLocationDisclosureConsents')
              AND name IN (N'UX_TripLocationDisclosureConsents_Active', N'IX_TripLocationDisclosureConsents_ActiveLookup');
            """);
        consentIndexCount.Should().Be(2);

        // Pre-existing, unrelated rows (a different session's log) are untouched by row count.
        var unrelatedSessionLogCount = await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM trip.TripLocationLogs WHERE session_id = {seed.OtherSessionId};
            """);
        unrelatedSessionLogCount.Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_EnforcesConsentInvariantsAfterUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await RevertToPreUc58SchemaAndSeedLegacyDataAsync(database);
        await database.ExecuteScriptAsync(GetMigrationPath());

        // Only 'OperationsStaff' is an accepted audience.
        Func<Task> invalidAudience = () => database.ExecuteNonQueryAsync($"""
            INSERT trip.TripLocationDisclosureConsents(session_id, user_id, audience, granted_at)
            VALUES ({seed.SessionId}, {seed.TravelerId}, 'GroupMembers', SYSUTCDATETIME());
            """);
        await invalidAudience.Should().ThrowAsync<SqlException>();

        // revoked_at must not precede granted_at.
        Func<Task> invalidInterval = () => database.ExecuteNonQueryAsync($"""
            INSERT trip.TripLocationDisclosureConsents(session_id, user_id, audience, granted_at, revoked_at)
            VALUES ({seed.SessionId}, {seed.TravelerId}, 'OperationsStaff', '2026-10-01', '2026-09-30');
            """);
        await invalidInterval.Should().ThrowAsync<SqlException>();

        // A valid grant succeeds.
        await database.ExecuteNonQueryAsync($"""
            INSERT trip.TripLocationDisclosureConsents(session_id, user_id, audience, granted_at)
            VALUES ({seed.SessionId}, {seed.TravelerId}, 'OperationsStaff', SYSUTCDATETIME());
            """);

        // A second active grant for the same (session, user, audience) violates the filtered
        // unique index: only one currently-active consent per subject per audience.
        Func<Task> duplicateActiveGrant = () => database.ExecuteNonQueryAsync($"""
            INSERT trip.TripLocationDisclosureConsents(session_id, user_id, audience, granted_at)
            VALUES ({seed.SessionId}, {seed.TravelerId}, 'OperationsStaff', SYSUTCDATETIME());
            """);
        await duplicateActiveGrant.Should().ThrowAsync<SqlException>();

        // Revoking the existing grant, then granting again, must succeed (the unique index is
        // filtered to revoked_at IS NULL).
        await database.ExecuteNonQueryAsync($"""
            UPDATE trip.TripLocationDisclosureConsents
            SET revoked_at = SYSUTCDATETIME()
            WHERE session_id = {seed.SessionId} AND user_id = {seed.TravelerId} AND audience = 'OperationsStaff';

            INSERT trip.TripLocationDisclosureConsents(session_id, user_id, audience, granted_at)
            VALUES ({seed.SessionId}, {seed.TravelerId}, 'OperationsStaff', SYSUTCDATETIME());
            """);

        var activeGrantCount = await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM trip.TripLocationDisclosureConsents
            WHERE session_id = {seed.SessionId} AND user_id = {seed.TravelerId}
              AND audience = 'OperationsStaff' AND revoked_at IS NULL;
            """);
        activeGrantCount.Should().Be(1);

        // Deleting the session cascades to its consent rows (ON DELETE CASCADE), consistent with
        // the other trip.* child tables.
        await database.ExecuteNonQueryAsync($"DELETE FROM trip.TripLocationLogs WHERE session_id = {seed.SessionId};");
        await database.ExecuteNonQueryAsync($"DELETE FROM trip.TripSessions WHERE session_id = {seed.SessionId};");
        var remainingConsentCount = await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM trip.TripLocationDisclosureConsents WHERE session_id = {seed.SessionId};
            """);
        remainingConsentCount.Should().Be(0);
    }

    private static async Task<SeedResult> RevertToPreUc58SchemaAndSeedLegacyDataAsync(SqlServerTestDatabase database)
    {
        // Undo exactly what this migration adds, so the database looks like it did immediately
        // before the migration ran in production.
        // The full schema creates these foreign keys inline (no explicit CONSTRAINT name), so
        // SQL Server auto-names them (e.g. FK__TripSessi__curre__xxxxxxxx) — look them up by the
        // column they constrain rather than assuming the migration script's own explicit names.
        await database.ExecuteNonQueryAsync("""
            IF OBJECT_ID(N'trip.TripLocationDisclosureConsents', N'U') IS NOT NULL
                DROP TABLE trip.TripLocationDisclosureConsents;

            DECLARE @sql NVARCHAR(MAX) = N'';

            SELECT @sql += N'ALTER TABLE trip.TripSessions DROP CONSTRAINT [' + fk.name + N'];'
            FROM sys.foreign_keys AS fk
            JOIN sys.foreign_key_columns AS fkc
                ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns AS col
                ON col.object_id = fkc.parent_object_id AND col.column_id = fkc.parent_column_id
            WHERE fk.parent_object_id = OBJECT_ID(N'trip.TripSessions')
              AND col.name = N'current_location_subject_user_id';
            IF @sql <> N'' EXEC sp_executesql @sql;
            IF COL_LENGTH(N'trip.TripSessions', N'current_location_subject_user_id') IS NOT NULL
                ALTER TABLE trip.TripSessions DROP COLUMN current_location_subject_user_id;

            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TripLocationLogs_SessionSubjectRecorded'
                AND object_id = OBJECT_ID(N'trip.TripLocationLogs'))
                DROP INDEX IX_TripLocationLogs_SessionSubjectRecorded ON trip.TripLocationLogs;

            SET @sql = N'';
            SELECT @sql += N'ALTER TABLE trip.TripLocationLogs DROP CONSTRAINT [' + fk.name + N'];'
            FROM sys.foreign_keys AS fk
            JOIN sys.foreign_key_columns AS fkc
                ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns AS col
                ON col.object_id = fkc.parent_object_id AND col.column_id = fkc.parent_column_id
            WHERE fk.parent_object_id = OBJECT_ID(N'trip.TripLocationLogs')
              AND col.name = N'subject_user_id';
            IF @sql <> N'' EXEC sp_executesql @sql;
            IF COL_LENGTH(N'trip.TripLocationLogs', N'subject_user_id') IS NOT NULL
                ALTER TABLE trip.TripLocationLogs DROP COLUMN subject_user_id;
            """);

        // Seed legacy data exactly as a pre-migration production database would have it: a real
        // session with its own location trail, and an unrelated session's trail, to prove the
        // migration does not cross-contaminate rows.
        await database.ExecuteNonQueryAsync("""
            DECLARE @traveler BIGINT;
            INSERT dbo.Users(role, email, full_name, status)
            VALUES ('Traveler', N'legacy-traveler@example.com', N'Legacy Traveler', 'Active');
            SET @traveler = SCOPE_IDENTITY();

            DECLARE @itinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Legacy plan', 'Active');
            SET @itinerary = SCOPE_IDENTITY();

            DECLARE @session BIGINT;
            INSERT trip.TripSessions(itinerary_id, requested_itinerary_id, traveler_user_id,
                start_idempotency_key, fsm_state,
                current_latitude, current_longitude, started_at, last_synced_at)
            VALUES (@itinerary, @itinerary, @traveler, NEWID(), 'Interrupted', 16.061200, 108.227700,
                '2026-09-25T17:00:00', '2026-09-26T03:12:44');
            SET @session = SCOPE_IDENTITY();

            INSERT trip.TripLocationLogs(session_id, latitude, longitude, recorded_at)
            VALUES (@session, 16.0601, 108.2266, '2026-09-26T03:10:00'),
                   (@session, 16.0602, 108.2267, '2026-09-26T03:11:00'),
                   (@session, 16.0603, 108.2268, '2026-09-26T03:12:00');

            DECLARE @otherItinerary BIGINT;
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@traveler, 'Manual', N'Other legacy plan', 'Completed');
            SET @otherItinerary = SCOPE_IDENTITY();

            DECLARE @otherSession BIGINT;
            INSERT trip.TripSessions(itinerary_id, requested_itinerary_id, traveler_user_id,
                start_idempotency_key, fsm_state, ended_at)
            VALUES (@otherItinerary, @otherItinerary, @traveler, NEWID(), 'Completed', '2026-09-20T10:00:00');
            SET @otherSession = SCOPE_IDENTITY();

            INSERT trip.TripLocationLogs(session_id, latitude, longitude, recorded_at)
            VALUES (@otherSession, 15.9, 108.1, '2026-09-20T09:00:00');

            SELECT @session, @otherSession, @traveler;
            """);

        var sessionId = await database.ExecuteScalarAsync<long>("""
            SELECT TOP (1) session_id FROM trip.TripSessions
            WHERE itinerary_id = (SELECT TOP (1) itinerary_id FROM planning.Itineraries WHERE title = N'Legacy plan');
            """);
        var otherSessionId = await database.ExecuteScalarAsync<long>("""
            SELECT TOP (1) session_id FROM trip.TripSessions
            WHERE itinerary_id = (SELECT TOP (1) itinerary_id FROM planning.Itineraries WHERE title = N'Other legacy plan');
            """);
        var travelerId = await database.ExecuteScalarAsync<long>("""
            SELECT TOP (1) user_id FROM dbo.Users WHERE email = N'legacy-traveler@example.com';
            """);
        var legacyLogCount = await database.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM trip.TripLocationLogs WHERE session_id = {sessionId};
            """);

        return new SeedResult(sessionId, otherSessionId, travelerId, legacyLogCount);
    }

    private sealed record SeedResult(long SessionId, long OtherSessionId, long TravelerId, int LegacyLogCount);

    private static string GetMigrationPath() =>
        Path.Combine(AppContext.BaseDirectory, "Database", "migrations", MigrationFileName);
}