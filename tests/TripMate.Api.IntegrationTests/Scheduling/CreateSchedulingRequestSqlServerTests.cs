using System.Data.Common;
using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Application.Features.Scheduling.Personalization;
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
    public async Task RateLimitRejected_WithNewKey_DoesNotPersistSchedulingRequest()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using var context = database.CreateDbContext();

        var result = await CreateHandler(
                context,
                generateRateLimiter: new RejectingGenerateRateLimiter())
            .Handle(CreateCommand(seed.UserId, Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationRateLimited);
        (await context.SchedulingRequests.CountAsync()).Should().Be(0);
        (await context.Itineraries.CountAsync()).Should().Be(0);
    }

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
        await ApplySchedulingMigrationsAsync(freshDatabase);

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
    public async Task GenerationReservationMigration_PreservesOutcomesAndRecoversLegacyProcessing()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.SchedulingRequests
                DROP CONSTRAINT CK_SchedulingRequests_GenerationReservation;
            ALTER TABLE planning.SchedulingRequests
                DROP CONSTRAINT DF_SchedulingRequests_GenerationAttempt;
            ALTER TABLE planning.SchedulingRequests
                DROP COLUMN generation_owner_id,
                            generation_lease_expires_at,
                            generation_attempt;
            """);
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO planning.SchedulingRequests (
                traveler_user_id, idempotency_key, request_hash, start_at, time_zone_id,
                start_latitude, start_longitude, destination_latitude, destination_longitude,
                return_to_start, available_minutes, transport_mode, search_radius_km,
                mandatory_poi_ids_json, rest_preference, status, requested_at,
                completed_at, failure_code, failure_message)
            VALUES
                ({seed.UserId}, NEWID(), REPLICATE('a', 64), '2026-10-20', 'Asia/Ho_Chi_Minh',
                 16, 108, 16, 108, 1, 480, 'Motorbike', 10, N'[]', 'None',
                 'Pending', '2026-10-01', NULL, NULL, NULL),
                ({seed.UserId}, NEWID(), REPLICATE('b', 64), '2026-10-20', 'Asia/Ho_Chi_Minh',
                 16, 108, 16, 108, 1, 480, 'Motorbike', 10, N'[]', 'None',
                 'Completed', '2026-10-01', '2026-10-02', NULL, NULL),
                ({seed.UserId}, NEWID(), REPLICATE('c', 64), '2026-10-20', 'Asia/Ho_Chi_Minh',
                 16, 108, 16, 108, 1, 480, 'Motorbike', 10, N'[]', 'None',
                 'Failed', '2026-10-01', '2026-10-02', 'planning.constraints_infeasible', N'No route.'),
                ({seed.UserId}, NEWID(), REPLICATE('d', 64), '2026-10-20', 'Asia/Ho_Chi_Minh',
                 16, 108, 16, 108, 1, 480, 'Motorbike', 10, N'[]', 'None',
                 'Processing', '2026-10-01', NULL, NULL, NULL);
            """);

        await ApplySchedulingMigrationsAsync(database);
        await ApplySchedulingMigrationsAsync(database);

        var outcomes = await database.ExecuteScalarAsync<string>("""
            SELECT STRING_AGG(
                CONCAT(status, N':',
                    COALESCE(failure_code, N'-'), N':',
                    COALESCE(failure_message, N'-'), N':',
                    CASE WHEN completed_at IS NULL THEN N'open' ELSE N'closed' END, N':',
                    CASE WHEN generation_owner_id IS NULL THEN N'no-owner' ELSE N'owner' END, N':',
                    CASE WHEN generation_lease_expires_at IS NULL THEN N'no-lease' ELSE N'lease' END, N':',
                    generation_attempt),
                N'|') WITHIN GROUP (ORDER BY request_id)
            FROM planning.SchedulingRequests;
            """);

        outcomes.Should().Be(
            "Pending:-:-:open:no-owner:no-lease:0"
            + "|Completed:-:-:closed:no-owner:no-lease:0"
            + "|Failed:planning.constraints_infeasible:No route.:closed:no-owner:no-lease:0"
            + "|Pending:-:-:open:no-owner:no-lease:0");
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
    public async Task SchedulingMigration_WhenNamedTransportModeCheckHasWrongShape_RejectsUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.SchedulingRequests
                DROP CONSTRAINT CK_SchedulingRequests_TransportMode;
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT CK_SchedulingRequests_TransportMode
                CHECK (transport_mode = 'Walking');
            """);

        Func<Task> applyMigration = () => ApplySchedulingMigrationsAsync(database);

        await applyMigration.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Scheduling schema contract mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SchedulingMigration_WhenNamedEndChoiceCheckHasWrongShape_RejectsUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.SchedulingRequests
                DROP CONSTRAINT CK_SchedulingRequests_EndChoice;
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT CK_SchedulingRequests_EndChoice
                CHECK (return_to_start = 1);
            """);

        Func<Task> applyMigration = () => ApplySchedulingMigrationsAsync(database);

        await applyMigration.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Scheduling schema contract mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SchedulingMigration_WhenNamedPoiCostCheckHasWrongShape_RejectsUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE catalog.POIs
                DROP CONSTRAINT CK_POIs_EstimatedVisitCost_NonNegative;
            ALTER TABLE catalog.POIs
                ADD CONSTRAINT CK_POIs_EstimatedVisitCost_NonNegative
                CHECK (estimated_visit_cost >= -1);
            """);

        Func<Task> applyMigration = () => ApplySchedulingMigrationsAsync(database);

        await applyMigration.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Scheduling schema contract mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SchedulingMigration_WhenNamedDefaultHasWrongShape_RejectsUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE planning.SchedulingRequests
                DROP CONSTRAINT DF_SchedulingRequests_MandatoryPoiIds;
            ALTER TABLE planning.SchedulingRequests
                ADD CONSTRAINT DF_SchedulingRequests_MandatoryPoiIds
                DEFAULT N'[1]' FOR mandatory_poi_ids_json;
            """);

        Func<Task> applyMigration = () => ApplySchedulingMigrationsAsync(database);

        await applyMigration.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Scheduling schema contract mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SchedulingMigration_WhenPoiCostCheckIsDisabledOrUntrusted_RejectsUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE catalog.POIs
                NOCHECK CONSTRAINT CK_POIs_EstimatedVisitCost_NonNegative;
            """);

        Func<Task> applyMigration = () => ApplySchedulingMigrationsAsync(database);

        await applyMigration.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Scheduling schema contract mismatch*");
    }

    [SqlServerTheory]
    [Trait("Category", "SqlServer")]
    [InlineData(
        "planning.SchedulingRequests",
        "DF_SchedulingRequests_TimeZoneId",
        "time_zone_id",
        "'asia/ho_chi_minh'")]
    [InlineData(
        "planning.SchedulingRequests",
        "DF_SchedulingRequests_RestPreference",
        "rest_preference",
        "'auto'")]
    [InlineData(
        "planning.SchedulingRequests",
        "DF_SchedulingRequests_MandatoryPoiIds",
        "mandatory_poi_ids_json",
        "'[]'")]
    [InlineData(
        "planning.SchedulingRequests",
        "DF_SchedulingRequests_ReturnToStart",
        "return_to_start",
        "1")]
    [InlineData(
        "planning.SchedulingRequests",
        "DF_SchedulingRequests_TransportMode",
        "transport_mode",
        "'walking'")]
    [InlineData(
        "planning.ItineraryItems",
        "DF_ItineraryItems_ItemKind",
        "item_kind",
        "'visit'")]
    public async Task SchedulingMigration_WhenCanonicalDefaultIsMissing_RepairsIt(
        string tableName,
        string constraintName,
        string columnName,
        string expectedDefinition)
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync(
            $"ALTER TABLE {tableName} DROP CONSTRAINT {constraintName};");

        await ApplySchedulingMigrationsAsync(database);
        await ApplySchedulingMigrationsAsync(database);

        var actualDefinition = await database.ExecuteScalarAsync<string>($"""
            SELECT LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
                defaults.definition, N'(', N''), N')', N''), N' ', N''), N'n''', N''''))
            FROM sys.default_constraints AS defaults
            INNER JOIN sys.columns AS columns
                ON columns.object_id = defaults.parent_object_id
               AND columns.column_id = defaults.parent_column_id
            WHERE defaults.name = N'{constraintName}'
              AND defaults.parent_object_id = OBJECT_ID(N'{tableName}')
              AND columns.name = N'{columnName}';
            """);

        actualDefinition.Should().Be(expectedDefinition);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SchedulingMigration_WhenManagedColumnHasWrongShape_RejectsUpgrade()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await database.ExecuteNonQueryAsync("""
            ALTER TABLE catalog.POIs
                ALTER COLUMN source_url NVARCHAR(100) NULL;
            """);

        Func<Task> applyMigration = () => ApplySchedulingMigrationsAsync(database);

        await applyMigration.Should()
            .ThrowAsync<SqlException>()
            .WithMessage("*Scheduling schema contract mismatch*");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Tm213_TravelerProfileRawSqlRoundTrip_MaterializesAllPreferenceFields()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync($"""
            INSERT INTO dbo.TravelerProfiles (
                user_id,
                interest_tags_json,
                preferred_transport_mode,
                travel_pace,
                risk_tolerance,
                food_preferences_json,
                default_budget)
            VALUES (
                {seed.UserId},
                N'["beach","cafe"]',
                'Motorbike',
                'Moderate',
                'Medium',
                N'["vegetarian"]',
                1000000.00);
            """);

        await using var context = database.CreateDbContext();
        var profile = await context.TravelerProfiles
            .AsNoTracking()
            .SingleAsync(item => item.UserId == seed.UserId);

        profile.InterestTagsJson.Should().Be("[\"beach\",\"cafe\"]");
        profile.PreferredTransportMode.Should().Be(TransportMode.Motorbike);
        profile.TravelPace.Should().Be(TravelerPace.Moderate);
        profile.RiskTolerance.Should().Be(RiskToleranceLevel.Medium);
        profile.FoodPreferencesJson.Should().Be("[\"vegetarian\"]");
        profile.DefaultBudget.Should().Be(1_000_000.00m);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Tm213_MatchingInterest_OutranksHigherScenicPoi()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedPreferenceScenarioAsync(database, includeProfile: true);

        long itineraryId;
        await using (var context = database.CreateDbContext())
        {
            var result = await CreateHandler(context).Handle(
                CreateCommand(seed.UserId, Guid.NewGuid()) with { AvailableMinutes = 150 },
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            itineraryId = result.Value.ItineraryId;
        }

        await using var verification = database.CreateDbContext();
        var persistedOrder = await verification.ItineraryItems
            .AsNoTracking()
            .Where(item => item.ItineraryId == itineraryId
                && item.Kind == ItineraryItemKind.Visit)
            .OrderBy(item => item.SequenceNo)
            .Select(item => item.PointOfInterestId)
            .ToArrayAsync();

        persistedOrder.Should().Equal(seed.PreferredPoiId);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Tm213_AbsentProfile_UsesFallbackScenicOrdering()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedPreferenceScenarioAsync(database, includeProfile: false);

        long itineraryId;
        await using (var context = database.CreateDbContext())
        {
            var result = await CreateHandler(context).Handle(
                CreateCommand(seed.UserId, Guid.NewGuid()) with { AvailableMinutes = 150 },
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            itineraryId = result.Value.ItineraryId;
        }

        await using var verification = database.CreateDbContext();
        var persistedOrder = await verification.ItineraryItems
            .AsNoTracking()
            .Where(item => item.ItineraryId == itineraryId
                && item.Kind == ItineraryItemKind.Visit)
            .OrderBy(item => item.SequenceNo)
            .Select(item => item.PointOfInterestId)
            .ToArrayAsync();

        persistedOrder.Should().Equal(seed.HigherScenicPoiId);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PoiLoad_AppliesSpatialAndExplicitIdPredicatesBeforeMaterialization()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        var interceptor = new TestCommandCounterInterceptor();
        long endPoiId;
        await using (var seedContext = database.CreateDbContext())
        {
            var category = await seedContext.PoiCategories.SingleAsync();
            var endPoi = CreateSelectablePoi(
                category,
                "Distant explicit end",
                21.0285m,
                105.8542m,
                seed.UserId,
                new FixedDateTimeProvider().UtcNow,
                "https://example.com/distant-explicit-end");
            seedContext.PointsOfInterest.Add(endPoi);
            await seedContext.SaveChangesAsync();
            endPoiId = endPoi.Id;
        }

        await using var context = database.CreateDbContext(interceptor);
        var result = await CreateHandler(context).Handle(
            CreateCommand(seed.UserId, Guid.NewGuid()) with
            {
                MandatoryPoiIds = [seed.PoiId],
                EndPoiId = endPoiId,
                ReturnToStart = false,
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        string[] poiLoads = interceptor.CommandTexts
            .Where(command => command.Contains("FROM [catalog].[POIs] AS [p]", StringComparison.Ordinal))
            .ToArray();
        poiLoads.Should().NotBeEmpty();
        poiLoads.Should().Contain(command =>
            command.Contains("[p].[status]", StringComparison.Ordinal)
            && command.Contains("[p].[latitude] >=", StringComparison.Ordinal)
            && command.Contains("[p].[latitude] <=", StringComparison.Ordinal)
            && command.Contains("[p].[longitude] >=", StringComparison.Ordinal)
            && command.Contains("[p].[longitude] <=", StringComparison.Ordinal)
            && (command.Contains("OR [p].[poi_id] = @mandatoryIds", StringComparison.Ordinal)
                || command.Contains("OR [p].[poi_id] IN (", StringComparison.Ordinal))
            && command.Contains(
                "OR [p].[poi_id] = @endPoiId_Value",
                StringComparison.Ordinal));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSameKey_CreatesOneItineraryAndReplaysOriginalResult()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var routeProvider = new BlockingCountingRouteDurationProvider();
        var rankingProvider = new CountingPoiRankingProvider();
        var reservationReads = new SchedulingReservationReadObserver();

        async Task<SchedulingResponseDto> CreateAsync()
        {
            await using var context = database.CreateDbContext(reservationReads);
            var result = await CreateHandler(
                    context,
                    routeProvider,
                    rankingProvider: rankingProvider,
                    rankingEnabled: true)
                .Handle(CreateCommand(seed.UserId, key), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            return result.Value;
        }

        var first = CreateAsync();
        await routeProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = CreateAsync();
        await reservationReads.CompetingRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
        routeProvider.CallCount.Should().Be(1);
        rankingProvider.CallCount.Should().Be(1);
        routeProvider.Release();
        var resultIds = await Task.WhenAll(first, second);

        resultIds[0].Should().BeEquivalentTo(resultIds[1], options => options.WithStrictOrdering());
        routeProvider.CallCount.Should().Be(1);
        rankingProvider.CallCount.Should().Be(1);
        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(1);
        (await verification.Itineraries.CountAsync()).Should().Be(1);
        (await verification.ItineraryItems.CountAsync()).Should().Be(resultIds[0].Items.Count);
        var request = await verification.SchedulingRequests.SingleAsync();
        request.Status.Should().Be(SchedulingRequestStatus.Completed);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConcurrentSameKeyWithDifferentPayloads_CreatesOneCanonicalResultAndReturnsConflict()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var routeProvider = new BlockingCountingRouteDurationProvider();

        async Task<(bool IsSuccess, string? ErrorCode, int? ItemCount)> CreateAsync(
            int availableMinutes)
        {
            await using var context = database.CreateDbContext();
            var result = await CreateHandler(context, routeProvider).Handle(
                CreateCommand(seed.UserId, key) with { AvailableMinutes = availableMinutes },
                CancellationToken.None);
            return (
                result.IsSuccess,
                result.ErrorCode,
                result.IsSuccess ? result.Value.Items.Count : null);
        }

        var first = CreateAsync(480);
        await routeProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = CreateAsync(420);
        (bool IsSuccess, string? ErrorCode, int? ItemCount) conflict = await second;
        conflict.IsSuccess.Should().BeFalse();
        conflict.ErrorCode.Should().Be(SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        routeProvider.Release();
        var results = await Task.WhenAll(first, second);

        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result =>
            !result.IsSuccess
            && result.ErrorCode == SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        routeProvider.CallCount.Should().Be(1);
        int canonicalItemCount = results.Single(result => result.IsSuccess).ItemCount!.Value;
        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(1);
        (await verification.Itineraries.CountAsync()).Should().Be(1);
        (await verification.ItineraryItems.CountAsync()).Should().Be(canonicalItemCount);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ExpiredLeaseTakeover_FencesStaleOwnerAndPersistsOneAuthoritativeItinerary()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        var key = Guid.NewGuid();
        var clock = new FixedDateTimeProvider();
        var staleRouteProvider = new BlockingCountingRouteDurationProvider();

        async Task<Result<SchedulingResponseDto>> RunStaleOwnerAsync()
        {
            await using var context = database.CreateDbContext();
            return await CreateHandler(context, staleRouteProvider, clock).Handle(
                CreateCommand(seed.UserId, key),
                CancellationToken.None);
        }

        Task<Result<SchedulingResponseDto>> staleOwner = RunStaleOwnerAsync();
        await staleRouteProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.UtcNow = clock.UtcNow.AddMinutes(2);

        Result<SchedulingResponseDto> takeover;
        await using (var takeoverContext = database.CreateDbContext())
        {
            takeover = await CreateHandler(takeoverContext, dateTimeProvider: clock).Handle(
                CreateCommand(seed.UserId, key),
                CancellationToken.None);
        }

        staleRouteProvider.Release();
        Result<SchedulingResponseDto> staleResult = await staleOwner.WaitAsync(
            TimeSpan.FromSeconds(10));

        takeover.IsSuccess.Should().BeTrue();
        staleResult.IsSuccess.Should().BeTrue();
        staleResult.Value.ItineraryId.Should().Be(takeover.Value.ItineraryId);
        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(1);
        (await verification.Itineraries.CountAsync()).Should().Be(1);
        var request = await verification.SchedulingRequests.SingleAsync();
        request.Status.Should().Be(SchedulingRequestStatus.Completed);
        request.GenerationAttempt.Should().Be(2);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ItineraryPersistenceFailure_RollsBackItineraryAndReleasesReservationForImmediateRetry()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        var seed = await SeedAsync(database);
        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE planning.Itineraries
                ADD CONSTRAINT [{RejectGeneratedItineraryConstraint}]
                CHECK (source_type <> 'CSPGenerated');
            """);

        var clock = new FixedDateTimeProvider();
        var key = Guid.NewGuid();
        await using (var context = database.CreateDbContext())
        {
            var action = () => CreateHandler(context, dateTimeProvider: clock).Handle(
                CreateCommand(seed.UserId, key),
                CancellationToken.None);
            await action.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(1);
        (await verification.Itineraries.CountAsync()).Should().Be(0);
        (await verification.ItineraryItems.CountAsync()).Should().Be(0);
        var request = await verification.SchedulingRequests.SingleAsync();
        request.Status.Should().Be(SchedulingRequestStatus.Pending);
        request.GenerationOwnerId.Should().BeNull();
        request.GenerationLeaseExpiresAtUtc.Should().BeNull();
        request.GenerationAttempt.Should().Be(1);

        await database.ExecuteNonQueryAsync($"""
            ALTER TABLE planning.Itineraries
                DROP CONSTRAINT [{RejectGeneratedItineraryConstraint}];
            """);
        await using var retryContext = database.CreateDbContext();
        var retry = await CreateHandler(retryContext, dateTimeProvider: clock).Handle(
            CreateCommand(seed.UserId, key),
            CancellationToken.None);
        retry.IsSuccess.Should().BeTrue();

        await verification.Entry(request).ReloadAsync();
        request.Status.Should().Be(SchedulingRequestStatus.Completed);
        request.GenerationAttempt.Should().Be(2);
        (await verification.Itineraries.CountAsync()).Should().Be(1);
    }

    private static CreateSchedulingRequestCommandHandler CreateHandler(
        ApplicationDbContext context,
        IRouteDurationProvider? routeDurationProvider = null,
        IDateTimeProvider? dateTimeProvider = null,
        IPoiRankingProvider? rankingProvider = null,
        bool rankingEnabled = false,
        IGenerateRateLimiter? generateRateLimiter = null) =>
        new(
            context,
            dateTimeProvider ?? new FixedDateTimeProvider(),
            routeDurationProvider ?? new FixedRouteDurationProvider(),
            new SqlServerSchedulingRequestLock(context),
            new PoiRankingOrchestrator(
                new PersonalBehaviorFeatureAggregator(context),
                rankingProvider ?? ProviderDisabledPoiRankingProvider.Instance,
                new PersonalizationRankingOptions(),
                providerEnabled: rankingEnabled,
                NullLogger<PoiRankingOrchestrator>.Instance),
            generateRateLimiter: generateRateLimiter);

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

    private static async Task<(long UserId, long PreferredPoiId, long HigherScenicPoiId)>
        SeedPreferenceScenarioAsync(
            SqlServerTestDatabase database,
            bool includeProfile)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        var user = new User
        {
            Email = "tm213-scheduling-sql@example.com",
            FullName = "TM-213 SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var preferredPoi = CreateSelectablePoi(
            PoiCategory.Create("Culture", null),
            "TM-213 preferred museum",
            16.0472m,
            108.2069m,
            user.Id,
            now,
            "https://example.com/tm213-preferred");
        var higherScenicPoi = CreateSelectablePoi(
            PoiCategory.Create("Nature", null),
            "TM-213 higher scenic attraction",
            16.0471m,
            108.2068m,
            user.Id,
            now,
            "https://example.com/tm213-scenic");
        context.PointsOfInterest.AddRange(preferredPoi, higherScenicPoi);
        await context.SaveChangesAsync();

        await database.ExecuteNonQueryAsync($"""
            UPDATE catalog.POIs
            SET scenic_score = CASE
                WHEN poi_id = {preferredPoi.Id} THEN 1.00
                WHEN poi_id = {higherScenicPoi.Id} THEN 9.00
                ELSE scenic_score
            END
            WHERE poi_id IN ({preferredPoi.Id}, {higherScenicPoi.Id});
            """);

        if (includeProfile)
        {
            await database.ExecuteNonQueryAsync($"""
                INSERT INTO dbo.TravelerProfiles (user_id, interest_tags_json)
                VALUES ({user.Id}, N'["culture"]');
                """);
        }

        return (user.Id, preferredPoi.Id, higherScenicPoi.Id);
    }

    private static PointOfInterest CreateSelectablePoi(
        PoiCategory category,
        string name,
        decimal latitude,
        decimal longitude,
        long createdById,
        DateTimeOffset now,
        string sourceUrl)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            createdById,
            now,
            averageVisitDurationMinutes: 60);
        poi.ConfigurePlanningMetadata(60_000m, sourceUrl, now);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            2,
            new TimeOnly(7, 0),
            new TimeOnly(20, 0),
            false));
        return poi;
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
                    "20261004_add_scheduling_generation_reservation.sql",
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
                       N'transport_mode', N'generation_owner_id',
                       N'generation_lease_expires_at', N'generation_attempt'))
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
                      N'CK_SchedulingRequests_GenerationReservation',
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
                      N'DF_SchedulingRequests_GenerationAttempt',
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
        public DateTimeOffset UtcNow { get; set; } =
            new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
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

    private sealed class BlockingCountingRouteDurationProvider : IRouteDurationProvider
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource Started => _started;

        public int CallCount => Volatile.Read(ref _callCount);

        public void Release() => _release.TrySetResult();

        public async Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            var durations = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    durations[row, column] = row == column ? 0 : 15;
                }
            }

            return RouteDurationMatrix.Create(durations);
        }
    }

    private sealed class CountingPoiRankingProvider : IPoiRankingProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<Result<PoiRankingResult>> RankAsync(
            PoiRankingRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(Result.Success(new PoiRankingResult(
                request.Candidates
                    .Select(candidate => new PoiRankingItem(candidate.PoiId, 0.5m, null))
                    .ToArray())));
        }
    }

    private sealed class RejectingGenerateRateLimiter : IGenerateRateLimiter
    {
        public ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
            long userId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GenerateRateLimitDecision(
                Allowed: false,
                ErrorCode: SchedulingErrorCodes.GenerationRateLimited,
                RetryAfterSeconds: 30));
    }

    private sealed class SchedulingReservationReadObserver : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _competingRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reservationReads;

        public TaskCompletionSource CompetingRead => _competingRead;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SchedulingRequests", StringComparison.Ordinal)
                && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _reservationReads) == 2)
            {
                _competingRead.TrySetResult();
            }

            return ValueTask.FromResult(result);
        }
    }
}