using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.AdjustItems;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Itineraries;

/// <summary>
/// HTTP contract of the Review 2 endpoints: access, status mapping and the problem-details extensions that
/// clients read to explain why a stop does not fit.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class Review2EndpointTests
{
    [Fact]
    public async Task ExternalReviews_WithoutAuthentication_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory(ApiTestAuthenticationMode.JwtBearer);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/pois/1/external-reviews");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExternalReviews_ForAnOperator_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.TourOperator);

        using var response = await client.GetAsync("/api/v1/pois/1/external-reviews");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExternalReviews_UnknownPoi_ReturnsNotFoundWithCode()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.GetAsync("/api/v1/pois/999999/external-reviews");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("external_reviews.poi_not_found");
    }

    [Fact]
    public async Task Translation_WhenTranslationIsOff_ReturnsServiceUnavailable()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.GetAsync("/api/v1/pois/1/external-reviews/7/translation?target=vi");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ErrorCodeAsync(response)).Should().Be("external_reviews.translation_unavailable");
    }

    [Fact]
    public async Task Suggestions_WithAnUnknownKind_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);

        using var response = await client.GetAsync("/api/v1/itineraries/1/suggestions?kind=Hotel&afterItemId=1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AdjustItems_StopThatDoesNotFit_Returns422WithTheStopAndReason()
    {
        var failure = Result.Failure<ItineraryDetailResponse>(
            ItineraryErrorCodes.StopInfeasible,
            "A selected location does not fit the itinerary.",
            new StopInfeasibility(88, StopInfeasibilityReasons.ClosedAtArrival, ClosesAt: new TimeOnly(14, 0))
                .ToErrorMetadata());
        await using var factory = new TripMateApiFactory(
            configureTestServices: services => services.AddScoped<ISender>(_ => new FixedSender(failure)));
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/itineraries/5/items")
        {
            Content = JsonContent.Create(new
            {
                stops = new object[]
                {
                    new { poiId = 12, transportModeToNext = "Walking" },
                    new { poiId = 88 },
                },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be(ItineraryErrorCodes.StopInfeasible);
        body.GetProperty("poiId").GetInt64().Should().Be(88);
        body.GetProperty("reason").GetString().Should().Be(StopInfeasibilityReasons.ClosedAtArrival);
        body.GetProperty("closesAt").GetString().Should().Be("14:00");
    }

    [Fact]
    public async Task AdjustItems_StopsBody_ReachesTheCommandWithLegModes()
    {
        var sender = new FixedSender(Result.Failure<ItineraryDetailResponse>(
            ItineraryErrorCodes.InvalidState,
            "stop here"));
        await using var factory = new TripMateApiFactory(
            configureTestServices: services => services.AddScoped<ISender>(_ => sender));
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/itineraries/5/items")
        {
            Content = JsonContent.Create(new
            {
                stops = new object[]
                {
                    new { poiId = 12, transportModeToNext = "Walking" },
                    new { poiId = 88 },
                },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        var command = sender.Received.OfType<AdjustItineraryItemsCommand>().Single();
        command.OrderedVisitPoiIds.Should().Equal(12L, 88L);
        command.EffectiveStops.Should().Equal(new ItineraryStop(12, TransportMode.Walking), new ItineraryStop(88));
    }

    [Fact]
    public async Task AdjustItems_WithBothStopsAndLegacyIds_ReturnsBadRequest()
    {
        var sender = new FixedSender(Result.Failure<ItineraryDetailResponse>(ItineraryErrorCodes.InvalidState, "unused"));
        await using var factory = new TripMateApiFactory(
            configureTestServices: services => services.AddScoped<ISender>(_ => sender));
        using var client = factory.CreateAuthenticatedClient(42, UserRole.Traveler);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/itineraries/5/items")
        {
            Content = JsonContent.Create(new
            {
                orderedVisitPoiIds = new[] { 12, 88 },
                stops = new object[] { new { poiId = 12 }, new { poiId = 88 } },
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        sender.Received.Should().BeEmpty();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("errorCode").GetString();
    }

    private sealed class FixedSender(object response) : ISender
    {
        public List<object> Received { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Received.Add(request);
            return Task.FromResult((TResponse)response);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(response);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}