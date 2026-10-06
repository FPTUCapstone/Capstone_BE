using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
public sealed class CreateSchedulingRequestRankingSqlServerTests
{
    private const decimal CenterLatitude = 16.0544m;
    private const decimal CenterLongitude = 108.2022m;

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AiDisabled_UsesBaseOrderingWithoutCallingProvider()
    {
        await using var database = await CreateDatabaseAsync();
        RankingSeed seed = await SeedAsync(database,
        [
            new PoiDefinition("Higher base", 9m, 5m, 10_000m),
            new PoiDefinition("Lower base", 1m, 5m, 10_000m),
        ]);
        var provider = new RecordingRankingProvider();

        long itineraryId = await ExecuteAsync(
            database,
            seed.UserId,
            Guid.NewGuid(),
            provider,
            providerEnabled: false);

        provider.CallCount.Should().Be(0);
        (await ReadVisitPoiIdsAsync(database, itineraryId)).Should().StartWith(
            [seed.OptionalPoiIds[0], seed.OptionalPoiIds[1]]);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task AiEnabled_ValidScoresReverseBaseOrdering()
    {
        await using var database = await CreateDatabaseAsync();
        RankingSeed seed = await SeedAsync(database,
        [
            new PoiDefinition("Higher base", 9m, 5m, 10_000m),
            new PoiDefinition("Lower base", 1m, 5m, 10_000m),
        ]);
        long higherBasePoiId = seed.OptionalPoiIds[0];
        long lowerBasePoiId = seed.OptionalPoiIds[1];
        var provider = new RecordingRankingProvider(request =>
            request.Candidates.Select(candidate => new PoiRankingItem(
                candidate.PoiId,
                candidate.PoiId == higherBasePoiId ? 0m : 1m,
                null)).ToArray());

        long itineraryId = await ExecuteThroughApiAsync(
            database,
            seed.UserId,
            Guid.NewGuid(),
            provider,
            new RecordingRouteDurationProvider());

        // Base: 0.30 vs 0.20. Effective: 0.18 vs 0.52 under the canonical 60/40 blend.
        provider.CallCount.Should().Be(1);
        provider.LastRequest!.Candidates.Select(candidate => candidate.PoiId)
            .Should().BeEquivalentTo(seed.OptionalPoiIds);
        (await ReadVisitPoiIdsAsync(database, itineraryId)).Should().StartWith(
            [lowerBasePoiId, higherBasePoiId]);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Replay_ReturnsPersistedResultWithoutAnotherProviderCall()
    {
        await using var database = await CreateDatabaseAsync();
        RankingSeed seed = await SeedAsync(database,
        [
            new PoiDefinition("Replay candidate", 5m, 5m, 10_000m),
        ]);
        var provider = new RecordingRankingProvider();
        var key = Guid.NewGuid();

        long firstItineraryId = await ExecuteAsync(
            database,
            seed.UserId,
            key,
            provider,
            providerEnabled: true);
        int callsBeforeReplay = provider.CallCount;

        long replayedItineraryId = await ExecuteAsync(
            database,
            seed.UserId,
            key,
            provider,
            providerEnabled: true);

        replayedItineraryId.Should().Be(firstItineraryId);
        (provider.CallCount - callsBeforeReplay).Should().Be(0);
        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(1);
        (await verification.Itineraries.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ProviderPool_DefaultBoundaryExcludesMandatoryAndSixtyFirstPoi()
    {
        await using var database = await CreateDatabaseAsync();
        PoiDefinition[] definitions = Enumerable.Range(1, 61)
            .Select(index => new PoiDefinition(
                $"Optional {index:D2}",
                5m,
                5m,
                10_000m,
                Latitude: CenterLatitude,
                Longitude: index == 61 ? CenterLongitude + 0.001m : CenterLongitude))
            .ToArray();
        RankingSeed seed = await SeedAsync(database, definitions, includeMandatory: true);
        long invalidatedPoiId = seed.OptionalPoiIds[0];
        long outsidePoolPoiId = seed.OptionalPoiIds[60];
        var provider = new RecordingRankingProvider(
            beforeReturn: _ => database.ExecuteNonQueryAsync($"""
                UPDATE catalog.POIs
                SET status = 'Inactive'
                WHERE poi_id = {invalidatedPoiId};
                """));
        var routeProvider = new RecordingRouteDurationProvider();

        long itineraryId = await ExecuteAsync(
            database,
            seed.UserId,
            Guid.NewGuid(),
            provider,
            providerEnabled: true,
            mandatoryPoiIds: [seed.MandatoryPoiId!.Value],
            routeProvider: routeProvider);

        provider.CallCount.Should().Be(1);
        provider.LastRequest!.Candidates.Should().HaveCount(60);
        provider.LastRequest.Candidates.Should().NotContain(candidate =>
            candidate.PoiId == seed.MandatoryPoiId.Value
            || candidate.PoiId == outsidePoolPoiId);
        routeProvider.LastPoints.Should().NotContain(point =>
            point.Latitude == CenterLatitude
            && point.Longitude == CenterLongitude + 0.001m);
        long[] visits = await ReadVisitPoiIdsAsync(database, itineraryId);
        visits.Should().Contain(seed.MandatoryPoiId.Value);
        visits.Should().NotContain(invalidatedPoiId);
        visits.Should().NotContain(outsidePoolPoiId);
    }

    [SqlServerTheory]
    [InlineData("inactive")]
    [InlineData("outside-radius")]
    [Trait("Category", "SqlServer")]
    public async Task PhaseTwoAuthoritativeInvalidation_DropsPooledPoiWithoutOutsidePoolBackfill(
        string invalidation)
    {
        await using var database = await CreateDatabaseAsync();
        RankingSeed seed = await SeedAsync(
            database,
            Enumerable.Range(1, 7)
                .Select(index => new PoiDefinition(
                    $"Race {index}",
                    8m - index,
                    5m,
                    10_000m,
                    Latitude: CenterLatitude,
                    Longitude: CenterLongitude + (index * 0.00001m)))
                .ToArray());
        long invalidatedPoiId = seed.OptionalPoiIds[0];
        long outsidePoolPoiId = seed.OptionalPoiIds[6];
        string mutation = invalidation == "inactive"
            ? "status = 'Inactive'"
            : "latitude = 17.000000, longitude = 109.000000";
        var provider = new RecordingRankingProvider(
            beforeReturn: _ => database.ExecuteNonQueryAsync($"""
                UPDATE catalog.POIs
                SET {mutation}
                WHERE poi_id = {invalidatedPoiId};
                """));
        var routeProvider = new RecordingRouteDurationProvider();
        var rankingOptions = new PersonalizationRankingOptions { MaxProviderCandidates = 6 };
        var generationOptions = new SchedulingGenerationOptions { MaxMatrixCandidates = 6 };

        long itineraryId = await ExecuteAsync(
            database,
            seed.UserId,
            Guid.NewGuid(),
            provider,
            providerEnabled: true,
            routeProvider: routeProvider,
            rankingOptions: rankingOptions,
            generationOptions: generationOptions);

        provider.CallCount.Should().Be(1);
        provider.LastRequest!.Candidates.Should().HaveCount(6);
        provider.LastRequest.Candidates.Should().NotContain(candidate =>
            candidate.PoiId == outsidePoolPoiId);
        routeProvider.LastPoints.Should().HaveCount(7,
            "five surviving pooled candidates plus start and end must be routed");
        long[] visits = await ReadVisitPoiIdsAsync(database, itineraryId);
        visits.Should().NotContain(invalidatedPoiId);
        visits.Should().NotContain(outsidePoolPoiId);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PhaseTwoQualityChange_DoesNotReorderFrozenRankingSnapshot()
    {
        await using var database = await CreateDatabaseAsync();
        RankingSeed seed = await SeedAsync(database,
        [
            new PoiDefinition("Frozen winner", 9m, 9m, 10_000m),
            new PoiDefinition("Current quality winner", 1m, 1m, 10_000m),
        ]);
        long frozenWinnerId = seed.OptionalPoiIds[0];
        long currentWinnerId = seed.OptionalPoiIds[1];
        var provider = new RecordingRankingProvider(
            beforeReturn: _ => database.ExecuteNonQueryAsync($"""
                UPDATE catalog.POIs
                SET scenic_score = CASE poi_id
                        WHEN {frozenWinnerId} THEN 0.00
                        WHEN {currentWinnerId} THEN 10.00
                    END,
                    photo_rating = CASE poi_id
                        WHEN {frozenWinnerId} THEN 0.00
                        WHEN {currentWinnerId} THEN 10.00
                    END
                WHERE poi_id IN ({frozenWinnerId}, {currentWinnerId});
                """));

        long itineraryId = await ExecuteAsync(
            database,
            seed.UserId,
            Guid.NewGuid(),
            provider,
            providerEnabled: true);

        provider.CallCount.Should().Be(1);
        (await ReadVisitPoiIdsAsync(database, itineraryId)).Should().StartWith(
            [frozenWinnerId, currentWinnerId]);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PhaseTwoMoveWithinRadius_RetainsPoiAndFrozenPoolMembership()
    {
        await using var database = await CreateDatabaseAsync();
        RankingSeed seed = await SeedAsync(
            database,
            Enumerable.Range(1, 7)
                .Select(index => new PoiDefinition(
                    $"Distance {index}",
                    5m,
                    5m,
                    10_000m,
                    Latitude: CenterLatitude,
                    Longitude: CenterLongitude + (index * 0.0001m)))
                .ToArray());
        long movedPoiId = seed.OptionalPoiIds[0];
        long outsidePoolPoiId = seed.OptionalPoiIds[6];
        decimal movedLongitude = CenterLongitude + 0.009m;
        decimal outsidePoolLongitude = CenterLongitude + 0.0007m;
        var provider = new RecordingRankingProvider(
            beforeReturn: _ => database.ExecuteNonQueryAsync($"""
                UPDATE catalog.POIs
                SET longitude = 108.211200
                WHERE poi_id = {movedPoiId};
                """));
        var routeProvider = new RecordingRouteDurationProvider();

        long itineraryId = await ExecuteAsync(
            database,
            seed.UserId,
            Guid.NewGuid(),
            provider,
            providerEnabled: true,
            routeProvider: routeProvider,
            rankingOptions: new PersonalizationRankingOptions { MaxProviderCandidates = 6 },
            generationOptions: new SchedulingGenerationOptions { MaxMatrixCandidates = 6 });

        provider.CallCount.Should().Be(1);
        provider.LastRequest!.Candidates.Should().HaveCount(6);
        provider.LastRequest.Candidates.Should().NotContain(candidate =>
            candidate.PoiId == outsidePoolPoiId);
        routeProvider.LastPoints.Should().Contain(point =>
            point.Latitude == CenterLatitude && point.Longitude == movedLongitude);
        routeProvider.LastPoints.Should().NotContain(point =>
            point.Latitude == CenterLatitude && point.Longitude == outsidePoolLongitude);
        (await ReadVisitPoiIdsAsync(database, itineraryId)).Should().Contain(movedPoiId);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task PhaseTwoCurrentCost_ControlsBudgetWhileFrozenCostRemainsRankingOnly()
    {
        await using var database = await CreateDatabaseAsync();
        RankingSeed seed = await SeedAsync(database,
        [
            new PoiDefinition("Frozen ranked winner", 9m, 9m, 10_000m),
            new PoiDefinition("Affordable survivor", 1m, 1m, 20_000m),
        ]);
        long expensivePoiId = seed.OptionalPoiIds[0];
        long affordablePoiId = seed.OptionalPoiIds[1];
        var provider = new RecordingRankingProvider(
            request => request.Candidates.Select(candidate => new PoiRankingItem(
                candidate.PoiId,
                candidate.PoiId == expensivePoiId ? 1m : 0m,
                null)).ToArray(),
            beforeReturn: _ => database.ExecuteNonQueryAsync($"""
                UPDATE catalog.POIs
                SET estimated_visit_cost = 200000.00
                WHERE poi_id = {expensivePoiId};
                """));

        long itineraryId = await ExecuteAsync(
            database,
            seed.UserId,
            Guid.NewGuid(),
            provider,
            providerEnabled: true,
            budgetVnd: 100_000m);

        provider.CallCount.Should().Be(1);
        long[] visits = await ReadVisitPoiIdsAsync(database, itineraryId);
        visits.Should().Contain(affordablePoiId);
        visits.Should().NotContain(expensivePoiId);
    }

    private static async Task<SqlServerTestDatabase> CreateDatabaseAsync()
    {
        SqlServerTestDatabase database = await SqlServerTestDatabase.CreateAsync();
        await ApplySchedulingMigrationsAsync(database);
        return database;
    }

    private static async Task<long> ExecuteAsync(
        SqlServerTestDatabase database,
        long userId,
        Guid key,
        RecordingRankingProvider provider,
        bool providerEnabled,
        IReadOnlyCollection<long>? mandatoryPoiIds = null,
        RecordingRouteDurationProvider? routeProvider = null,
        PersonalizationRankingOptions? rankingOptions = null,
        SchedulingGenerationOptions? generationOptions = null,
        decimal budgetVnd = 800_000m)
    {
        await using var context = database.CreateDbContext();
        var handler = new CreateSchedulingRequestCommandHandler(
            context,
            new FixedDateTimeProvider(),
            routeProvider ?? new RecordingRouteDurationProvider(),
            new SqlServerSchedulingRequestLock(context),
            new PoiRankingOrchestrator(
                new PersonalBehaviorFeatureAggregator(context),
                provider,
                rankingOptions ?? new PersonalizationRankingOptions(),
                providerEnabled,
                NullLogger<PoiRankingOrchestrator>.Instance),
            generationOptions);
        var result = await handler.Handle(
            CreateCommand(userId, key, mandatoryPoiIds ?? [], budgetVnd),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Value.ItineraryId;
    }

    private static async Task<long> ExecuteThroughApiAsync(
        SqlServerTestDatabase database,
        long userId,
        Guid key,
        RecordingRankingProvider provider,
        RecordingRouteDurationProvider routeProvider)
    {
        await using var factory = new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedDateTimeProvider(),
            configureTestServices: services =>
            {
                services.RemoveAll<IRouteDurationProvider>();
                services.AddSingleton<IRouteDurationProvider>(routeProvider);
                services.RemoveAll<PoiRankingOrchestrator>();
                services.AddScoped(serviceProvider => new PoiRankingOrchestrator(
                    new PersonalBehaviorFeatureAggregator(
                        serviceProvider.GetRequiredService<IApplicationDbContext>()),
                    provider,
                    new PersonalizationRankingOptions(),
                    providerEnabled: true,
                    serviceProvider.GetRequiredService<
                        Microsoft.Extensions.Logging.ILogger<PoiRankingOrchestrator>>()));
            });
        using HttpClient client = factory.CreateAuthenticatedClient(userId, UserRole.Traveler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scheduling-requests")
        {
            Content = JsonContent.Create(new
            {
                startAt = "2026-10-20T08:00:00+07:00",
                timeZoneId = "Asia/Ho_Chi_Minh",
                startLatitude = CenterLatitude,
                startLongitude = CenterLongitude,
                explorationLatitude = CenterLatitude,
                explorationLongitude = CenterLongitude,
                returnToStart = true,
                availableMinutes = 480,
                transportMode = "Motorbike",
                searchRadiusKm = 10m,
                budgetVnd = 800_000m,
                mandatoryPoiIds = Array.Empty<long>(),
                restPreference = "None",
            }),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());

        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using Stream body = await response.Content.ReadAsStreamAsync();
        using JsonDocument payload = await JsonDocument.ParseAsync(body);
        return payload.RootElement.GetProperty("itineraryId").GetInt64();
    }

    private static async Task<RankingSeed> SeedAsync(
        SqlServerTestDatabase database,
        IReadOnlyCollection<PoiDefinition> definitions,
        bool includeMandatory = false)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        string suffix = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Email = $"tm215-ranking-{suffix}@example.test",
            FullName = "TM-215 SQL Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var category = PoiCategory.Create($"TM-215-{suffix}", null);
        var optionals = definitions
            .Select((definition, index) => CreatePoi(
                category,
                $"{definition.Name}-{suffix}-{index}",
                definition.Latitude ?? CenterLatitude,
                definition.Longitude ?? CenterLongitude,
                definition.Cost,
                user.Id,
                now))
            .ToArray();
        PointOfInterest? mandatory = includeMandatory
            ? CreatePoi(
                category,
                $"Mandatory-{suffix}",
                CenterLatitude + 0.0005m,
                CenterLongitude + 0.0005m,
                0m,
                user.Id,
                now)
            : null;
        context.PointsOfInterest.AddRange(optionals);
        if (mandatory is not null)
        {
            context.PointsOfInterest.Add(mandatory);
        }

        await context.SaveChangesAsync();

        foreach ((PointOfInterest poi, PoiDefinition definition) in optionals.Zip(definitions))
        {
            await database.ExecuteNonQueryAsync($"""
                UPDATE catalog.POIs
                SET scenic_score = {definition.ScenicScore},
                    photo_rating = {definition.PhotoRating}
                WHERE poi_id = {poi.Id};
                """);
        }

        return new RankingSeed(
            user.Id,
            optionals.Select(poi => poi.Id).ToArray(),
            mandatory?.Id);
    }

    private static PointOfInterest CreatePoi(
        PoiCategory category,
        string name,
        decimal latitude,
        decimal longitude,
        decimal cost,
        long createdById,
        DateTimeOffset now)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
            createdById,
            now,
            averageVisitDurationMinutes: 30);
        poi.ConfigurePlanningMetadata(cost, $"https://example.test/{poi.GetHashCode()}", now);
        poi.AddOpeningHour(PoiOpeningHour.Create(
            2,
            new TimeOnly(7, 0),
            new TimeOnly(20, 0),
            false));
        return poi;
    }

    private static CreateSchedulingRequestCommand CreateCommand(
        long userId,
        Guid key,
        IReadOnlyCollection<long> mandatoryPoiIds,
        decimal budgetVnd) => new(
        userId,
        key,
        new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(7)),
        "Asia/Ho_Chi_Minh",
        CenterLatitude,
        CenterLongitude,
        CenterLatitude,
        CenterLongitude,
        null,
        true,
        480,
        TransportMode.Motorbike,
        10m,
        budgetVnd,
        mandatoryPoiIds,
        RestPreference.None);

    private static async Task<long[]> ReadVisitPoiIdsAsync(
        SqlServerTestDatabase database,
        long itineraryId)
    {
        await using var context = database.CreateDbContext();
        return await context.ItineraryItems
            .AsNoTracking()
            .Where(item => item.ItineraryId == itineraryId
                && item.Kind == ItineraryItemKind.Visit)
            .OrderBy(item => item.SequenceNo)
            .Select(item => item.PointOfInterestId!.Value)
            .ToArrayAsync();
    }

    private static async Task ApplySchedulingMigrationsAsync(SqlServerTestDatabase database)
    {
        foreach (string fileName in new[]
                 {
                     "20260914_add_scheduling_request_generation.sql",
                    "20260915_extend_scheduling_request_contract.sql",
                    "20260919_allow_named_rest_items.sql",
                    "20261004_add_scheduling_generation_reservation.sql",
                 })
        {
            string migrationPath = Path.Combine(
                AppContext.BaseDirectory,
                "Database",
                "migrations",
                fileName);
            string migration = await File.ReadAllTextAsync(migrationPath);
            migration = Regex.Replace(
                migration,
                @"^\s*GO\s*$",
                string.Empty,
                RegexOptions.Multiline);
            await database.ExecuteNonQueryAsync(migration);
        }
    }

    private sealed record PoiDefinition(
        string Name,
        decimal ScenicScore,
        decimal PhotoRating,
        decimal Cost,
        decimal? Latitude = null,
        decimal? Longitude = null);

    private sealed record RankingSeed(
        long UserId,
        IReadOnlyList<long> OptionalPoiIds,
        long? MandatoryPoiId);

    private sealed class FixedDateTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingRouteDurationProvider : IRouteDurationProvider
    {
        public IReadOnlyList<RoutePoint> LastPoints { get; private set; } = [];

        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            LastPoints = points.ToArray();
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

    private sealed class RecordingRankingProvider(
        Func<PoiRankingRequest, IReadOnlyCollection<PoiRankingItem>>? rank = null,
        Func<PoiRankingRequest, Task>? beforeReturn = null) : IPoiRankingProvider
    {
        public int CallCount { get; private set; }

        public PoiRankingRequest? LastRequest { get; private set; }

        public async Task<Result<PoiRankingResult>> RankAsync(
            PoiRankingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            if (beforeReturn is not null)
            {
                await beforeReturn(request);
            }

            IReadOnlyCollection<PoiRankingItem> ranked = rank?.Invoke(request)
                ?? request.Candidates
                    .Select(candidate => new PoiRankingItem(candidate.PoiId, 0m, null))
                    .ToArray();
            return Result.Success(new PoiRankingResult(ranked));
        }
    }
}