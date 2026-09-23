using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateSchedulingRequestSqlServerTests
{
    private const string RejectGeneratedItineraryConstraint =
        "CK_Itineraries_RejectGeneratedForSchedulingRollback";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CanonicalSchema_AndSchedulingMigrations_ExposeSameSchedulingConstraints()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);

        var schedulingColumns = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
              AND name IN (
                  N'idempotency_key', N'request_hash', N'start_at', N'time_zone_id',
                  N'end_poi_id', N'return_to_start', N'transport_mode',
                  N'rest_preference', N'failure_code', N'failure_message');
            """);
        var hasOperationIndex = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'planning.SchedulingRequests')
              AND name = N'UX_SchedulingRequests_Traveler_Key';
            """);
        var hasEndPoiForeignKey = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
              AND name = N'FK_SchedulingRequests_EndPoi';
            """);
        var hasItemKindConstraint = await database.ExecuteScalarAsync<int>("""
            SELECT COUNT(*)
            FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'planning.ItineraryItems')
              AND name = N'CK_ItineraryItems_KindPoi';
            """);

        schedulingColumns.Should().Be(10);
        hasOperationIndex.Should().Be(1);
        hasEndPoiForeignKey.Should().Be(1);
        hasItemKindConstraint.Should().Be(1);
        var writeNegativeCost = () => database.ExecuteNonQueryAsync($"""
            UPDATE catalog.POIs
            SET estimated_visit_cost = -1
            WHERE poi_id = {seed.PoiId};
            """);
        await writeNegativeCost.Should().ThrowAsync<SqlException>();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task LegacySchedulingSchema_Upgrade_ConvergesWithFreshSchedulingRequestContract()
    {
        await using var upgradedDatabase = await SqlServerTestDatabase.CreateEmptyAsync();
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Database",
            "Fixtures",
            "tm56_pre_migration_schema.sql");
        await upgradedDatabase.ExecuteScriptAsync(fixturePath);
        await upgradedDatabase.ExecuteNonQueryAsync("""
            INSERT INTO dbo.Users DEFAULT VALUES;
            INSERT INTO planning.SchedulingRequests (
                traveler_user_id, start_latitude, start_longitude,
                destination_latitude, destination_longitude, available_minutes,
                search_radius_km, mandatory_poi_ids_json)
            VALUES (1, 16.054400, 108.202200, NULL, NULL, 480, NULL, NULL);
            """);

        await ApplySchedulingMigrationsAsync(upgradedDatabase);
        await ApplySchedulingMigrationsAsync(upgradedDatabase);
        await using var freshDatabase = await SqlServerTestDatabase.CreateAsync();

        (await SchedulingColumnInventoryAsync(upgradedDatabase))
            .Should().Be(await SchedulingColumnInventoryAsync(freshDatabase));
        (await SchedulingConstraintInventoryAsync(upgradedDatabase))
            .Should().Be(await SchedulingConstraintInventoryAsync(freshDatabase));
        var backfilledValues = await upgradedDatabase.ExecuteScalarAsync<string>("""
            SELECT CONCAT(
                CONVERT(VARCHAR(20), destination_latitude), N'|',
                CONVERT(VARCHAR(20), destination_longitude), N'|',
                CONVERT(VARCHAR(20), search_radius_km), N'|',
                mandatory_poi_ids_json)
            FROM planning.SchedulingRequests
            WHERE request_id = 1;
            """);
        backfilledValues.Should().Be("16.054400|108.202200|10.00|[]");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SchedulingMigration_WhenNamedObjectsHaveWrongShape_RejectsUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.SchedulingRequests
                DROP CONSTRAINT CK_SchedulingRequests_RestPreference;
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT CK_SchedulingRequests_RestPreference
                CHECK (rest_preference = 'Auto');
            DROP INDEX UX_SchedulingRequests_Traveler_Key
                ON planning.SchedulingRequests;
            CREATE UNIQUE INDEX UX_SchedulingRequests_Traveler_Key
                ON planning.SchedulingRequests(idempotency_key, traveler_user_id);
            """);

        Func<Task> applyMigration = () => ApplySchedulingMigrationsAsync(database);

        await applyMigration.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Scheduling schema contract mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SchedulingMigration_WhenNamedEndPoiForeignKeyHasWrongTarget_RejectsUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.SchedulingRequests
                DROP CONSTRAINT FK_SchedulingRequests_EndPoi;
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT FK_SchedulingRequests_EndPoi
                FOREIGN KEY (end_poi_id) REFERENCES dbo.Users(user_id);
            """);

        Func<Task> applyMigration = () => ApplySchedulingMigrationsAsync(database);

        await applyMigration.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Scheduling schema contract mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSameKey_CreatesOneItineraryAndReplaysOriginalResult()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<long> CreateAsync()
        {
            await startGate.Task;
            await using var context = database.CreateDbContext();
            var result = await CreateHandler(context).Handle(CreateCommand(seed.UserId, key), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value.ItineraryId;
        }

        var first = CreateAsync();
        var second = CreateAsync();
        startGate.SetResult();
        var resultIds = await Task.WhenAll(first, second);

        resultIds[0].Should().Be(resultIds[1]);
        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(1);
        (await verification.Itineraries.CountAsync()).Should().Be(1);
        (await verification.ItineraryItems.CountAsync()).Should().BeGreaterThan(0);
        var request = await verification.SchedulingRequests.SingleAsync();
        request.Status.Should().Be(SchedulingRequestStatus.Completed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ItineraryPersistenceFailure_RollsBackRequestItineraryAndItems()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE planning.Itineraries
                ADD CONSTRAINT [{RejectGeneratedItineraryConstraint}]
                CHECK (source_type <> 'CSPGenerated');
            """);

        await using (var context = database.CreateDbContext())
        {
            var action = () => CreateHandler(context).Handle(
                CreateCommand(seed.UserId, Guid.NewGuid()),
                CancellationToken.None);
            await action.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(0);
        (await verification.Itineraries.CountAsync()).Should().Be(0);
        (await verification.ItineraryItems.CountAsync()).Should().Be(0);
    }

    private static CreateSchedulingRequestCommandHandler CreateHandler(ApplicationDbContext context) =>
        new(
            context,
            new FixedDateTimeProvider(),
            new FixedRouteDurationProvider(),
            new SqlServerSchedulingRequestLock(context));

    private static async Task<(long UserId, long PoiId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var user = new User
        {
            Email = "scheduling-sql@example.com",
            FullName = "Scheduling SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var category = PoiCategory.Create("Culture", null);
        var poi = PointOfInterest.Create(
            category,
            "Scheduling SQL Museum",
            16.0471m,
            108.2068m,
            user.Id,
            now,
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, "https://example.com/scheduling-sql", now);
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));

        context.PointsOfInterest.Add(poi);
        await context.SaveChangesAsync();
        return (user.Id, poi.Id);
    }

    private static CreateSchedulingRequestCommand CreateCommand(long userId, Guid key) => new(
        userId,
        key,
        new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(7)),
        "Asia/Ho_Chi_Minh",
        16.0544m,
        108.2022m,
        16.0471m,
        108.2068m,
        null,
        true,
        480,
        TransportMode.Motorbike,
        10m,
        800_000m,
        [],
        RestPreference.None);

    private static async Task ApplySchedulingMigrationsAsync(SqlServerTestDatabase database)
    {
        foreach (var fileName in new[]
                 {
                     "20260914_add_scheduling_request_generation.sql",
                     "20260915_extend_scheduling_request_contract.sql",
                     "20260919_allow_named_rest_items.sql",
                 })
        {
            var migrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "Database",
                "migrations",
                fileName);
            var migration = await File.ReadAllTextAsync(migrationPath);
            migration = Regex.Replace(migration, @"^\s*GO\s*$", string.Empty, RegexOptions.Multiline);
            await database.ExecuteNonQueryAsync(migration);
        }
    }

    private static Task<string> SchedulingColumnInventoryAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<string>("""
            SELECT STRING_AGG(
                CONCAT(SCHEMA_NAME([object].schema_id), N'.', [object].name, N'.', columns.name,
                    N':', TYPE_NAME(columns.system_type_id), N':', columns.max_length,
                    N':', columns.precision, N':', columns.scale,
                    N':', is_nullable, N':', is_identity, N':', is_computed),
                N'|') WITHIN GROUP (ORDER BY [object].name, columns.name)
            FROM sys.columns AS columns
            INNER JOIN sys.objects AS [object]
                ON [object].object_id = columns.object_id
            WHERE (columns.object_id = OBJECT_ID(N'planning.SchedulingRequests')
                   AND columns.name IN (
                       N'start_at', N'time_zone_id', N'idempotency_key', N'request_hash',
                       N'rest_preference', N'failure_code', N'failure_message',
                       N'destination_latitude', N'destination_longitude', N'search_radius_km',
                       N'mandatory_poi_ids_json', N'end_poi_id', N'return_to_start',
                       N'transport_mode'))
               OR (columns.object_id = OBJECT_ID(N'planning.ItineraryItems')
                   AND columns.name IN (N'item_kind', N'poi_id'))
               OR (columns.object_id = OBJECT_ID(N'catalog.POIs')
                   AND columns.name IN (N'estimated_visit_cost', N'source_url', N'verified_at'));
            """);

    private static Task<string> SchedulingConstraintInventoryAsync(SqlServerTestDatabase database) =>
        database.ExecuteScalarAsync<string>("""
            SELECT STRING_AGG(CONCAT(constraint_type, N':', name), N'|')
                WITHIN GROUP (ORDER BY constraint_type, name)
            FROM (
                SELECT
                    N'CHECK' AS constraint_type,
                    CONCAT(SCHEMA_NAME([object].schema_id), N'.', [object].name, N'.', checks.name,
                        N':', checks.is_disabled, N':', checks.is_not_trusted,
                    N':', LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                        OBJECT_DEFINITION(checks.object_id), N'[', N''), N']', N''), N'(', N''), N')', N''),
                        N' ', N''), CHAR(13), N''), CHAR(10), N''))) AS name
                FROM sys.check_constraints AS checks
                INNER JOIN sys.objects AS [object]
                    ON [object].object_id = checks.parent_object_id
                WHERE checks.name IN (
                      N'CK_SchedulingRequests_TransportMode',
                      N'CK_SchedulingRequests_RestPreference',
                      N'CK_SchedulingRequests_EndChoice',
                      N'CK_ItineraryItems_KindPoi',
                      N'CK_POIs_EstimatedVisitCost_NonNegative')
                UNION ALL
                SELECT
                    N'DEFAULT' AS constraint_type,
                    CONCAT(SCHEMA_NAME([object].schema_id), N'.', [object].name, N'.', columns.name,
                        N':', defaults.name, N':', LOWER(REPLACE(REPLACE(REPLACE(
                        defaults.definition, N'(', N''), N')', N''), N' ', N''))) AS name
                FROM sys.default_constraints AS defaults
                INNER JOIN sys.columns AS columns
                    ON columns.object_id = defaults.parent_object_id
                    AND columns.column_id = defaults.parent_column_id
                INNER JOIN sys.objects AS [object]
                    ON [object].object_id = defaults.parent_object_id
                WHERE defaults.parent_object_id IN (
                    OBJECT_ID(N'planning.SchedulingRequests'),
                    OBJECT_ID(N'planning.ItineraryItems'))
                  AND defaults.name IN (
                      N'DF_SchedulingRequests_TimeZoneId',
                      N'DF_SchedulingRequests_ReturnToStart',
                      N'DF_SchedulingRequests_TransportMode',
                      N'DF_SchedulingRequests_MandatoryPoiIds',
                      N'DF_SchedulingRequests_RestPreference',
                      N'DF_ItineraryItems_ItemKind')
                UNION ALL
                SELECT
                    N'INDEX' AS constraint_type,
                    CONCAT(SCHEMA_NAME([object].schema_id), N'.', [object].name, N'.', [index].name,
                        N':', [index].is_unique, N':', [index].is_disabled, N':', [index].has_filter,
                        N':', INDEX_COL(N'planning.SchedulingRequests', [index].index_id, 1),
                        N':', INDEX_COL(N'planning.SchedulingRequests', [index].index_id, 2),
                        N':', COALESCE(INDEX_COL(N'planning.SchedulingRequests', [index].index_id, 3), N'<NULL>')) AS name
                FROM sys.indexes AS [index]
                INNER JOIN sys.objects AS [object]
                    ON [object].object_id = [index].object_id
                WHERE [index].object_id = OBJECT_ID(N'planning.SchedulingRequests')
                  AND [index].name = N'UX_SchedulingRequests_Traveler_Key'
                UNION ALL
                SELECT
                    N'FOREIGN_KEY' AS constraint_type,
                    CONCAT(SCHEMA_NAME([object].schema_id), N'.', [object].name, N'.', foreign_key.name,
                        N':', foreign_key.is_disabled, N':', foreign_key.is_not_trusted,
                        N':', COL_NAME(column_map.parent_object_id, column_map.parent_column_id),
                    N'->', OBJECT_SCHEMA_NAME(foreign_key.referenced_object_id), N'.',
                    OBJECT_NAME(foreign_key.referenced_object_id), N'.',
                    COL_NAME(column_map.referenced_object_id, column_map.referenced_column_id)) AS name
                FROM sys.foreign_keys AS foreign_key
                INNER JOIN sys.foreign_key_columns AS column_map
                    ON column_map.constraint_object_id = foreign_key.object_id
                INNER JOIN sys.objects AS [object]
                    ON [object].object_id = foreign_key.parent_object_id
                WHERE foreign_key.parent_object_id = OBJECT_ID(N'planning.SchedulingRequests')
                  AND foreign_key.name = N'FK_SchedulingRequests_EndPoi') AS scheduling_constraints;
            """);

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedRouteDurationProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var durations = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 15;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(durations));
        }
    }
}