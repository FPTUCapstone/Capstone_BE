using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateSchedulingRequestExplanationSqlServerTests
{
    private const decimal CenterLatitude = 16.0544m;
    private const decimal CenterLongitude = 108.2022m;

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateSchedulingRequest_ProviderEnabledAndSucceeds_PersistsAndReturnsExplanation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeedData seed = await SeedAsync(database);
        var provider = new RecordingExplanationProvider();
        await using var factory = CreateFactory(database, provider, providerEnabled: true);
        Guid key = Guid.NewGuid();

        var (response, payload) = await PostSchedulingRequestAsync(
            factory,
            seed.UserId,
            key,
            [seed.MandatoryPoiId]);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        provider.CallCount.Should().Be(1);

        JsonElement items = payload.RootElement.GetProperty("items");
        items.GetArrayLength().Should().BeGreaterThan(0);
        foreach (JsonElement item in items.EnumerateArray())
        {
            int seq = item.GetProperty("sequenceNo").GetInt32();
            item.GetProperty("friendlyExplanation").GetString().Should().Be($"Giải thích AI cho mục {seq}.");
            item.GetProperty("recommendationReason").GetString().Should().NotBeNullOrWhiteSpace();
        }

        long itineraryId = payload.RootElement.GetProperty("itineraryId").GetInt64();
        await using var verification = database.CreateDbContext();
        var persistedItems = await verification.ItineraryItems
            .AsNoTracking()
            .Where(i => i.ItineraryId == itineraryId)
            .OrderBy(i => i.SequenceNo)
            .ToListAsync();

        persistedItems.Should().NotBeEmpty();
        foreach (var persisted in persistedItems)
        {
            persisted.FriendlyExplanation.Should().Be($"Giải thích AI cho mục {persisted.SequenceNo}.");
            persisted.RecommendationReason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateSchedulingRequest_Replay_ReturnsPersistedExplanationWithoutNewProviderCall()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeedData seed = await SeedAsync(database);
        var provider = new RecordingExplanationProvider();
        await using var factory = CreateFactory(database, provider, providerEnabled: true);
        Guid key = Guid.NewGuid();

        var (firstResponse, firstPayload) = await PostSchedulingRequestAsync(
            factory,
            seed.UserId,
            key,
            [seed.MandatoryPoiId]);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        provider.CallCount.Should().Be(1);

        var (replayResponse, replayPayload) = await PostSchedulingRequestAsync(
            factory,
            seed.UserId,
            key,
            [seed.MandatoryPoiId]);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Replay must NOT call explanation provider again
        provider.CallCount.Should().Be(1);

        long firstItineraryId = firstPayload.RootElement.GetProperty("itineraryId").GetInt64();
        long replayItineraryId = replayPayload.RootElement.GetProperty("itineraryId").GetInt64();
        replayItineraryId.Should().Be(firstItineraryId);

        JsonElement firstItems = firstPayload.RootElement.GetProperty("items");
        JsonElement replayItems = replayPayload.RootElement.GetProperty("items");
        replayItems.GetArrayLength().Should().Be(firstItems.GetArrayLength());

        for (int i = 0; i < firstItems.GetArrayLength(); i++)
        {
            replayItems[i].GetProperty("friendlyExplanation").GetString().Should().Be(
                firstItems[i].GetProperty("friendlyExplanation").GetString());
            replayItems[i].GetProperty("recommendationReason").GetString().Should().Be(
                firstItems[i].GetProperty("recommendationReason").GetString());
        }

        await using var verification = database.CreateDbContext();
        (await verification.SchedulingRequests.CountAsync()).Should().Be(1);
        (await verification.Itineraries.CountAsync()).Should().Be(1);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateSchedulingRequest_ProviderFails_PersistsAndReturnsDeterministicFallback()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeedData seed = await SeedAsync(database);
        var provider = new RecordingExplanationProvider(
            _ => Result.Failure<ItineraryExplanationResult>(ExplanationProviderErrorCodes.ServerError, "simulated error"));
        await using var factory = CreateFactory(database, provider, providerEnabled: true);
        Guid key = Guid.NewGuid();

        var (response, payload) = await PostSchedulingRequestAsync(
            factory,
            seed.UserId,
            key,
            [seed.MandatoryPoiId]);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        provider.CallCount.Should().Be(1);

        JsonElement items = payload.RootElement.GetProperty("items");
        items.GetArrayLength().Should().BeGreaterThan(0);
        foreach (JsonElement item in items.EnumerateArray())
        {
            AssertContextAwareFallback(item, seed);
            item.GetProperty("recommendationReason").GetString().Should().NotBeNullOrWhiteSpace();
        }

        long itineraryId = payload.RootElement.GetProperty("itineraryId").GetInt64();
        await using var verification = database.CreateDbContext();
        var persistedItems = await verification.ItineraryItems
            .AsNoTracking()
            .Where(i => i.ItineraryId == itineraryId)
            .OrderBy(i => i.SequenceNo)
            .ToListAsync();

        persistedItems.Should().HaveSameCount(items.EnumerateArray());
        foreach (var persisted in persistedItems)
        {
            JsonElement responseItem = items.EnumerateArray()
                .Single(item => item.GetProperty("sequenceNo").GetInt32() == persisted.SequenceNo);
            persisted.FriendlyExplanation.Should().Be(
                responseItem.GetProperty("friendlyExplanation").GetString());
            persisted.RecommendationReason.Should().NotBeNullOrWhiteSpace();
            persisted.RecommendationReason.Should().Be(
                responseItem.GetProperty("recommendationReason").GetString());
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateSchedulingRequest_ProviderDisabled_PersistsAndReturnsFallbackWithoutCallingProvider()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        SeedData seed = await SeedAsync(database);
        var provider = new RecordingExplanationProvider();
        await using var factory = CreateFactory(database, provider, providerEnabled: false);
        Guid key = Guid.NewGuid();

        var (response, payload) = await PostSchedulingRequestAsync(
            factory,
            seed.UserId,
            key,
            [seed.MandatoryPoiId]);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        provider.CallCount.Should().Be(0);

        JsonElement items = payload.RootElement.GetProperty("items");
        items.GetArrayLength().Should().BeGreaterThan(0);
        foreach (JsonElement item in items.EnumerateArray())
        {
            AssertContextAwareFallback(item, seed);
        }

        long itineraryId = payload.RootElement.GetProperty("itineraryId").GetInt64();
        await using var verification = database.CreateDbContext();
        var persistedItems = await verification.ItineraryItems
            .AsNoTracking()
            .Where(i => i.ItineraryId == itineraryId)
            .ToListAsync();

        persistedItems.Should().HaveSameCount(items.EnumerateArray());
        foreach (var persisted in persistedItems)
        {
            JsonElement responseItem = items.EnumerateArray()
                .Single(item => item.GetProperty("sequenceNo").GetInt32() == persisted.SequenceNo);
            persisted.FriendlyExplanation.Should().Be(
                responseItem.GetProperty("friendlyExplanation").GetString());
            persisted.RecommendationReason.Should().Be(
                responseItem.GetProperty("recommendationReason").GetString());
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task CreateSchedulingRequest_ProviderEnabledVsDisabled_OnlyFriendlyExplanationDiffers()
    {
        await using var enabledDb = await SqlServerTestDatabase.CreateAsync();
        await using var disabledDb = await SqlServerTestDatabase.CreateAsync();
        SeedData enabledSeed = await SeedAsync(enabledDb);
        SeedData disabledSeed = await SeedAsync(disabledDb);

        var enabledProvider = new RecordingExplanationProvider();
        var disabledProvider = new RecordingExplanationProvider();

        await using var enabledFactory = CreateFactory(enabledDb, enabledProvider, providerEnabled: true);
        await using var disabledFactory = CreateFactory(disabledDb, disabledProvider, providerEnabled: false);

        Guid key = Guid.NewGuid();
        var (enabledResponse, enabledPayload) = await PostSchedulingRequestAsync(
            enabledFactory,
            enabledSeed.UserId,
            key,
            [enabledSeed.MandatoryPoiId]);
        var (disabledResponse, disabledPayload) = await PostSchedulingRequestAsync(
            disabledFactory,
            disabledSeed.UserId,
            key,
            [disabledSeed.MandatoryPoiId]);

        enabledResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        disabledResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        enabledProvider.CallCount.Should().Be(1);
        disabledProvider.CallCount.Should().Be(0);

        JsonElement enabledItems = enabledPayload.RootElement.GetProperty("items");
        JsonElement disabledItems = disabledPayload.RootElement.GetProperty("items");

        enabledItems.GetArrayLength().Should().Be(disabledItems.GetArrayLength());
        for (int i = 0; i < enabledItems.GetArrayLength(); i++)
        {
            JsonElement e = enabledItems[i];
            JsonElement d = disabledItems[i];

            e.GetProperty("sequenceNo").GetInt32().Should().Be(d.GetProperty("sequenceNo").GetInt32());
            e.GetProperty("poiId").GetRawText().Should().Be(d.GetProperty("poiId").GetRawText());
            e.GetProperty("itemKind").GetString().Should().Be(d.GetProperty("itemKind").GetString());
            e.GetProperty("plannedArrival").GetDateTimeOffset().Should().Be(d.GetProperty("plannedArrival").GetDateTimeOffset());
            e.GetProperty("plannedDeparture").GetDateTimeOffset().Should().Be(d.GetProperty("plannedDeparture").GetDateTimeOffset());
            e.GetProperty("stayDurationMinutes").GetInt32().Should().Be(d.GetProperty("stayDurationMinutes").GetInt32());
            e.GetProperty("travelDurationToNextMinutes").GetRawText().Should().Be(d.GetProperty("travelDurationToNextMinutes").GetRawText());
            e.GetProperty("estimatedCost").GetDecimal().Should().Be(d.GetProperty("estimatedCost").GetDecimal());
            e.GetProperty("isMandatory").GetBoolean().Should().Be(d.GetProperty("isMandatory").GetBoolean());
            e.GetProperty("recommendationReason").GetString().Should().Be(d.GetProperty("recommendationReason").GetString());

            // ONLY FriendlyExplanation may differ
            e.GetProperty("friendlyExplanation").GetString().Should().NotBe(d.GetProperty("friendlyExplanation").GetString());
            e.GetProperty("friendlyExplanation").GetString().Should().StartWith("Giải thích AI");
            AssertContextAwareFallback(d, disabledSeed);
        }
    }

    private static void AssertContextAwareFallback(JsonElement item, SeedData seed)
    {
        string explanation = item.GetProperty("friendlyExplanation").GetString()!;
        explanation.Should().NotBeNullOrWhiteSpace();
        explanation.Length.Should().BeLessThanOrEqualTo(500);

        if (item.GetProperty("poiId").ValueKind == JsonValueKind.Number
            && item.GetProperty("poiId").GetInt64() == seed.MandatoryPoiId)
        {
            explanation.Should().Contain(seed.MandatoryPoiName);
            explanation.Should().Contain(seed.CategoryName);
        }
    }

    private static TripMateApiFactory CreateFactory(
        SqlServerTestDatabase database,
        RecordingExplanationProvider provider,
        bool providerEnabled)
    {
        return new TripMateApiFactory(
            sqlServerConnectionString: database.ConnectionString,
            dateTimeProviderFactory: _ => new FixedDateTimeProvider(),
            explanationProviderFactory: _ => provider,
            explanationProviderEnabled: providerEnabled,
            configureTestServices: services =>
            {
                services.RemoveAll<IRouteDurationProvider>();
                services.AddSingleton<IRouteDurationProvider>(new FixedRouteDurationProvider());
            });
    }

    private static async Task<(HttpResponseMessage Response, JsonDocument Payload)> PostSchedulingRequestAsync(
        TripMateApiFactory factory,
        long userId,
        Guid idempotencyKey,
        IReadOnlyList<long>? mandatoryPoiIds = null)
    {
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
                mandatoryPoiIds = mandatoryPoiIds ?? Array.Empty<long>(),
                restPreference = "None",
            }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());

        HttpResponseMessage response = await client.SendAsync(request);
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        JsonDocument doc = await JsonDocument.ParseAsync(stream);
        return (response, doc);
    }

    private static async Task<SeedData> SeedAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();
        var now = new FixedDateTimeProvider().UtcNow;
        string suffix = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Email = $"tm217-explanation-{suffix}@example.test",
            FullName = "TM-217 Explanation Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var category = PoiCategory.Create($"Cat-{suffix}", null);
        var mandatoryPoi = PointOfInterest.Create(
            category,
            $"Mandatory-{suffix}",
            CenterLatitude,
            CenterLongitude,
            user.Id,
            now,
            averageVisitDurationMinutes: 60);
        mandatoryPoi.ConfigurePlanningMetadata(50_000m, $"https://example.test/{mandatoryPoi.GetHashCode()}", now);
        mandatoryPoi.AddOpeningHour(PoiOpeningHour.Create(
            2,
            new TimeOnly(7, 0),
            new TimeOnly(20, 0),
            false));

        context.PointsOfInterest.Add(mandatoryPoi);
        await context.SaveChangesAsync();

        return new SeedData(user.Id, mandatoryPoi.Id, mandatoryPoi.Name, category.Name);
    }

    private sealed record SeedData(
        long UserId,
        long MandatoryPoiId,
        string MandatoryPoiName,
        string CategoryName);

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

    private sealed class RecordingExplanationProvider(
        Func<ItineraryExplanationInput, Result<ItineraryExplanationResult>>? resultFactory = null)
        : IItineraryExplanationProvider
    {
        public int CallCount { get; private set; }
        public ItineraryExplanationInput? LastInput { get; private set; }

        public Task<Result<ItineraryExplanationResult>> ExplainAsync(
            ItineraryExplanationInput input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastInput = input;
            Result<ItineraryExplanationResult> result = resultFactory?.Invoke(input)
                ?? Result.Success(new ItineraryExplanationResult(
                    input.Items.Select(item => new ItineraryExplanationItemResult(
                        item.SequenceNo,
                        item.PoiId,
                        $"Giải thích AI cho mục {item.SequenceNo}.")).ToArray()));
            return Task.FromResult(result);
        }
    }
}