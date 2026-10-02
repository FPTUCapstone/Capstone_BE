using System.Net;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutDetailsEndpointTests
{
    [Fact]
    public async Task GetDetails_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/payouts/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDetails_Traveler_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Traveler);

        var response = await client.GetAsync("/api/v1/admin/payouts/1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDetails_UnknownPayout_ReturnsNotFoundWithLockedMsg128()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/payouts/999");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().Contain("Payouts.NotFound");
        body.Should().Contain("No records found matching your criteria.");
    }

    [Fact]
    public async Task GetDetails_MalformedSegment_BypassesRouteAndReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/payouts/1abc");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDetails_NonPositivePayoutId_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var zero = await client.GetAsync("/api/v1/admin/payouts/0");
        var negative = await client.GetAsync("/api/v1/admin/payouts/-1");

        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        negative.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}