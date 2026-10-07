using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Scheduling;

[Collection(nameof(TripMateApiFactory))]
public sealed class CreateSchedulingRequestEndpointTests
{
    [Fact]
    public async Task Post_SameKeyAndPayload_ReplaysTheOriginalItinerary()
    {
        using var factory = new TripMateApiFactory();
        await SeedSelectablePoiAsync(factory);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        var idempotencyKey = Guid.NewGuid();

        using var first = await SendValidRequestAsync(client, idempotencyKey);
        var countsAfterFirst = await ReadSchedulingCountsAsync(factory);
        using var replay = await SendValidRequestAsync(client, idempotencyKey);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        var firstPayload = await first.Content.ReadFromJsonAsync<SchedulingResponseDto>(serializerOptions);
        var replayPayload = await replay.Content.ReadFromJsonAsync<SchedulingResponseDto>(serializerOptions);
        replayPayload.Should().BeEquivalentTo(firstPayload, options => options.WithStrictOrdering());
        replayPayload!.SchedulingRequestId.Should().Be(firstPayload!.SchedulingRequestId);
        replayPayload!.ItineraryId.Should().Be(firstPayload!.ItineraryId);
        (await ReadSchedulingCountsAsync(factory)).Should().Be(countsAfterFirst);
    }

    [Fact]
    public async Task Post_SameKeyWithDifferentPayload_ReturnsConflictWithoutNewRows()
    {
        using var factory = new TripMateApiFactory();
        await SeedSelectablePoiAsync(factory);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        var idempotencyKey = Guid.NewGuid();

        using var first = await SendValidRequestAsync(client, idempotencyKey);
        var countsAfterFirst = await ReadSchedulingCountsAsync(factory);
        using var mismatch = await SendValidRequestAsync(client, idempotencyKey, availableMinutes: 420);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        mismatch.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using JsonDocument problem = JsonDocument.Parse(await mismatch.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(SchedulingErrorCodes.IdempotencyKeyPayloadMismatch);
        (await ReadSchedulingCountsAsync(factory)).Should().Be(countsAfterFirst);
    }

    [Fact]
    public async Task Post_DifferentKeyWithSamePayload_CreatesIndependentOperations()
    {
        using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<IGenerateRateLimiter>();
            services.AddSingleton<IGenerateRateLimiter, AllowAllGenerateRateLimiter>();
        });
        await SeedSelectablePoiAsync(factory);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var first = await SendValidRequestAsync(client, Guid.NewGuid());
        using var second = await SendValidRequestAsync(client, Guid.NewGuid());

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        var firstPayload = await first.Content.ReadFromJsonAsync<SchedulingResponseDto>(serializerOptions);
        var secondPayload = await second.Content.ReadFromJsonAsync<SchedulingResponseDto>(serializerOptions);
        secondPayload!.SchedulingRequestId.Should().NotBe(firstPayload!.SchedulingRequestId);
        secondPayload.ItineraryId.Should().NotBe(firstPayload.ItineraryId);
        (await ReadSchedulingCountsAsync(factory)).SchedulingRequests.Should().Be(2);
    }

    [Fact]
    public async Task Post_WithoutIdempotencyKey_ReturnsBadRequest()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/scheduling-requests",
            CreateRequestBody());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Post_WithoutMandatoryPoiIds_CreatesAnItinerary()
    {
        using var factory = new TripMateApiFactory();
        await SeedSelectablePoiAsync(factory);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scheduling-requests")
        {
            Content = JsonContent.Create(new
            {
                startAt = "2026-10-20T08:00:00+07:00",
                timeZoneId = "Asia/Ho_Chi_Minh",
                startLatitude = 16.0544m,
                startLongitude = 108.2022m,
                explorationLatitude = 16.0471m,
                explorationLongitude = 108.2068m,
                endPoiId = (long?)null,
                returnToStart = true,
                availableMinutes = 480,
                transportMode = "Motorbike",
                searchRadiusKm = 10m,
                budgetVnd = 800000m,
                restPreference = "None",
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Post_AsUnauthenticatedCaller_ReturnsUnauthorized()
    {
        using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scheduling-requests")
        {
            Content = JsonContent.Create(CreateRequestBody()),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_WhenRoutingProviderTimesOut_ReturnsSafeServiceUnavailableWithoutPartialRows()
    {
        using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<IRouteDurationProvider>();
            services.AddSingleton<IRouteDurationProvider>(new TimedOutRouteDurationProvider());
        });
        await SeedSelectablePoiAsync(factory);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await SendValidRequestAsync(client, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        string responseBody = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(SchedulingErrorCodes.RoutingProviderUnavailable);
        problem.RootElement.GetProperty("title").GetString()
            .Should().Be("The routing service is temporarily unavailable. Please try again.");
        responseBody.Should().NotContain("sensitive transport detail");
        responseBody.ToLowerInvariant().Should().NotContain("stacktrace");
        responseBody.ToLowerInvariant().Should().NotContain("api_key");

        var rowCounts = await factory.WithDbContextAsync(async dbContext => new
        {
            SchedulingRequests = await dbContext.SchedulingRequests.CountAsync(),
            Itineraries = await dbContext.Itineraries.CountAsync(),
            ItineraryItems = await dbContext.ItineraryItems.CountAsync(),
        });
        rowCounts.SchedulingRequests.Should().Be(1);
        rowCounts.Itineraries.Should().Be(0);
        rowCounts.ItineraryItems.Should().Be(0);
    }

    [Fact]
    public async Task Post_WhenRateLimiterUnavailable_Returns503WithoutPartialRows()
    {
        using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<IGenerateRateLimiter>();
            services.AddSingleton<IGenerateRateLimiter>(new FixedDecisionGenerateRateLimiter(
                new GenerateRateLimitDecision(
                    Allowed: false,
                    ErrorCode: SchedulingErrorCodes.GenerationRateLimiterUnavailable)));
        });
        await SeedSelectablePoiAsync(factory);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await SendValidRequestAsync(client, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.Should().NotContainKey("Retry-After");
        string responseBody = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        problem.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(SchedulingErrorCodes.GenerationRateLimiterUnavailable);
        problem.RootElement.GetProperty("title").GetString()
            .Should().Be("Itinerary generation is temporarily unavailable. Please try again.");

        var rowCounts = await factory.WithDbContextAsync(async dbContext => new
        {
            SchedulingRequests = await dbContext.SchedulingRequests.CountAsync(),
            Itineraries = await dbContext.Itineraries.CountAsync(),
            ItineraryItems = await dbContext.ItineraryItems.CountAsync(),
        });
        rowCounts.SchedulingRequests.Should().Be(0);
        rowCounts.Itineraries.Should().Be(0);
        rowCounts.ItineraryItems.Should().Be(0);
    }

    [Theory]
    [InlineData(SchedulingErrorCodes.GenerationRateLimited, 45)]
    [InlineData(SchedulingErrorCodes.GenerationCooldown, 15)]
    public async Task Post_WhenHealthyRateLimitRejects_Returns429WithRetryAfterHeader(
        string errorCode,
        int retryAfterSeconds)
    {
        using var factory = new TripMateApiFactory(configureTestServices: services =>
        {
            services.RemoveAll<IGenerateRateLimiter>();
            services.AddSingleton<IGenerateRateLimiter>(new FixedDecisionGenerateRateLimiter(
                new GenerateRateLimitDecision(
                    Allowed: false,
                    ErrorCode: errorCode,
                    RetryAfterSeconds: retryAfterSeconds)));
        });
        await SeedSelectablePoiAsync(factory);
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await SendValidRequestAsync(client, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.TryGetValues("Retry-After", out var retryAfterValues).Should().BeTrue();
        retryAfterValues!.Should().ContainSingle().Which.Should().Be(retryAfterSeconds.ToString());
        string responseBody = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(responseBody);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(429);
        problem.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        var rowCounts = await ReadSchedulingCountsAsync(factory);
        rowCounts.SchedulingRequests.Should().Be(0);
        rowCounts.Itineraries.Should().Be(0);
        rowCounts.ItineraryItems.Should().Be(0);
    }

    private static async Task<HttpResponseMessage> SendValidRequestAsync(
        HttpClient client,
        Guid idempotencyKey,
        int availableMinutes = 480)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scheduling-requests")
        {
            Content = JsonContent.Create(CreateRequestBody(availableMinutes)),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
        return await client.SendAsync(request);
    }

    private static object CreateRequestBody(int availableMinutes = 480) => new
    {
        startAt = "2026-10-20T08:00:00+07:00",
        timeZoneId = "Asia/Ho_Chi_Minh",
        startLatitude = 16.0544m,
        startLongitude = 108.2022m,
        explorationLatitude = 16.0471m,
        explorationLongitude = 108.2068m,
        endPoiId = (long?)null,
        returnToStart = true,
        availableMinutes,
        transportMode = "Motorbike",
        searchRadiusKm = 10m,
        budgetVnd = 800000m,
        mandatoryPoiIds = Array.Empty<long>(),
        restPreference = "None",
    };

    private static Task<(int SchedulingRequests, int Itineraries, int ItineraryItems)>
        ReadSchedulingCountsAsync(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async dbContext => (
            await dbContext.SchedulingRequests.CountAsync(),
            await dbContext.Itineraries.CountAsync(),
            await dbContext.ItineraryItems.CountAsync()));

    private static async Task SeedSelectablePoiAsync(TripMateApiFactory factory)
    {
        await factory.WithDbContextAsync(async dbContext =>
        {
            var category = PoiCategory.Create("Culture", null);
            var poi = PointOfInterest.Create(
                category,
                "Cham Museum",
                16.0471m,
                108.2068m,
                1,
                DateTimeOffset.UtcNow,
                averageVisitDurationMinutes: 60);
            poi.ConfigurePlanningMetadata(60_000m, "https://example.com/cham", DateTimeOffset.UtcNow);
            poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(7, 0), new TimeOnly(20, 0), false));
            dbContext.PointsOfInterest.Add(poi);
            await dbContext.SaveChangesAsync();
            return true;
        });
    }

    private sealed class TimedOutRouteDurationProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) =>
            Task.FromException<RouteDurationMatrix>(new RouteDurationProviderException(
                "OpenRouteService",
                RouteDurationProviderFailureKind.Timeout,
                "safe provider failure",
                new TaskCanceledException("sensitive transport detail")));
    }

    private sealed class AllowAllGenerateRateLimiter : IGenerateRateLimiter
    {
        public ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
            long userId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GenerateRateLimitDecision(true));
    }

    private sealed class FixedDecisionGenerateRateLimiter(GenerateRateLimitDecision decision)
        : IGenerateRateLimiter
    {
        public ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
            long userId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(decision);
    }
}