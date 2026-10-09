using System.Net;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.ActiveTrips;

[Collection(nameof(TripMateApiFactory))]
public sealed class ActiveTripDetailsEndpointTests
{
    [Fact]
    public async Task GetDetails_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/trips/active/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDetails_Traveler_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/admin/trips/active/1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDetails_AdministratorWithUnknownSession_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/trips/active/999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDetails_NonPositiveSessionId_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        // Segments matching {tripId:long} but ≤ 0 reach the validator and fail with 400.
        var zero = await client.GetAsync("/api/v1/admin/trips/active/0");
        var negative = await client.GetAsync("/api/v1/admin/trips/active/-1");

        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        negative.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetDetails_MalformedSegment_BypassesRouteAndReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        // Non-integer segments never match the {tripId:long} constraint; the framework
        // answers 404 before the application sees the request.
        var response = await client.GetAsync("/api/v1/admin/trips/active/1abc");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}