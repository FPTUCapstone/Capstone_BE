using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
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
        using var replay = await SendValidRequestAsync(client, idempotencyKey);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        var firstPayload = await first.Content.ReadFromJsonAsync<SchedulingResponseDto>(serializerOptions);
        var replayPayload = await replay.Content.ReadFromJsonAsync<SchedulingResponseDto>(serializerOptions);
        replayPayload!.ItineraryId.Should().Be(firstPayload!.ItineraryId);
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

    private static async Task<HttpResponseMessage> SendValidRequestAsync(HttpClient client, Guid idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scheduling-requests")
        {
            Content = JsonContent.Create(CreateRequestBody()),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
        return await client.SendAsync(request);
    }

    private static object CreateRequestBody() => new
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
        mandatoryPoiIds = Array.Empty<long>(),
        restPreference = "None",
    };

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
}