using FluentAssertions;

using Microsoft.Data.SqlClient;

namespace TripMate.Api.IntegrationTests.Infrastructure;

[Collection(nameof(TripMateApiFactory))]
public sealed class GroupLeaveMigrationSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task NavigationReasonMigration_AllowsRevocation_AndIsSafeToRerun()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var migration = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Database", "migrations",
            "20261025_uc21_navigation_access_revoked_reason.sql"));
        await database.ExecuteNonQueryAsync(migration);
        await database.ExecuteNonQueryAsync("""
            INSERT dbo.Users(role, email, full_name)
            VALUES ('Traveler', N'uc21-nav-reason@example.com', N'UC21 Traveler');
            DECLARE @travelerId BIGINT = SCOPE_IDENTITY();
            INSERT planning.Itineraries(traveler_user_id, source_type, title, status)
            VALUES (@travelerId, 'Manual', N'UC21 navigation', 'Active');
            DECLARE @itineraryId BIGINT = SCOPE_IDENTITY();
            INSERT trip.TripSessions(
                itinerary_id, requested_itinerary_id, traveler_user_id, start_idempotency_key,
                fsm_state, completion_reason, started_at, ended_at)
            VALUES (
                @itineraryId, @itineraryId, @travelerId, NEWID(),
                'Completed', 'GroupAccessRevoked', '2026-10-25T01:00:00', '2026-10-25T02:00:00');
            """);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM trip.TripSessions
            WHERE completion_reason = 'GroupAccessRevoked';
            """)).Should().Be(1);
        // apply-schema.sh reapplies every historical migration on an existing database.
        await database.ExecuteScriptAsync(Path.Combine(
            AppContext.BaseDirectory, "Database", "migrations",
            "20261008_add_uc13_navigation_execution.sql"));
        await database.ExecuteNonQueryAsync(migration);
        (await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'trip.TripSessions')
              AND name = N'CK_TripSessions_CompletionReason'
              AND is_disabled = 0 AND is_not_trusted = 0;
            """)).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_BackfillsValidOpenGroups_AndIsSafeToRerun()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ApplyLegacySchemaAndSeedAsync(database, activeHost: true);
        var migrationPath = GetMigrationPath();

        await database.ExecuteScriptAsync(migrationPath);
        await database.ExecuteScriptAsync(migrationPath);

        (await database.ExecuteScalarAsync<string>("SELECT status FROM social.TravelGroups WHERE group_id = 1"))
            .Should().Be("Active");
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM social.GroupLeaveOperations"))
            .Should().Be(0);

        var key = Guid.NewGuid();
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO social.GroupLeaveOperations
                (traveler_user_id, group_id, idempotency_key, group_closed, left_at)
            VALUES (1, 1, '{key}', 1, '2026-10-25T09:00:00');
            """);
        await database.ExecuteNonQueryAsync("""
            INSERT INTO social.TravelGroups (itinerary_id, host_user_id, name, status)
            VALUES (1, 1, 'Another trip', 'Active');
            """);
        var duplicateKey = () => database.ExecuteNonQueryAsync($"""
            INSERT INTO social.GroupLeaveOperations
                (traveler_user_id, group_id, idempotency_key, group_closed, left_at)
            VALUES (1, 2, '{key}', 1, '2026-10-25T09:01:00');
            """);
        await duplicateKey.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Migration_RejectsLegacyGroupWithNoActiveHost()
    {
        await using var database = await SqlServerTestDatabase.CreateEmptyAsync();
        await ApplyLegacySchemaAndSeedAsync(database, activeHost: false);

        var migrationPath = GetMigrationPath();
        var act = () => database.ExecuteScriptAsync(migrationPath);

        await act.Should().ThrowAsync<SqlException>();
        (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('social.TravelGroups') AND name = 'status'"))
            .Should().Be(0);
    }

    private static async Task ApplyLegacySchemaAndSeedAsync(SqlServerTestDatabase database, bool activeHost)
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Database", "tripmate_schema_v7.sql");
        await database.ExecuteScriptAsync(schemaPath);
        await database.ExecuteNonQueryAsync($"""
            SET IDENTITY_INSERT dbo.Users ON;
            INSERT INTO dbo.Users (user_id, role, email, full_name, status)
            VALUES (1, 'Traveler', 'uc21-migration@example.com', 'UC21 Host', 'Active');
            SET IDENTITY_INSERT dbo.Users OFF;
            SET IDENTITY_INSERT planning.Itineraries ON;
            INSERT INTO planning.Itineraries (itinerary_id, traveler_user_id, source_type, title, status, version)
            VALUES (1, 1, 'Manual', 'Legacy itinerary', 'Active', 1);
            SET IDENTITY_INSERT planning.Itineraries OFF;
            SET IDENTITY_INSERT social.TravelGroups ON;
            INSERT INTO social.TravelGroups (group_id, itinerary_id, host_user_id, name)
            VALUES (1, 1, 1, 'Legacy group');
            SET IDENTITY_INSERT social.TravelGroups OFF;
            INSERT INTO social.GroupMembers (group_id, user_id, status)
            VALUES (1, 1, '{(activeHost ? "Active" : "Left")}');
            """);
    }

    private static string GetMigrationPath() => Path.Combine(
        AppContext.BaseDirectory, "Database", "migrations", "20261025_uc21_group_leave_lifecycle.sql");
}