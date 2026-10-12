using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutSettlementEndpointTests
{
    [Fact]
    public async Task Get_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/payout-settlements");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Staff_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        var response = await client.GetAsync("/api/v1/admin/payout-settlements");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_AdministratorWithNoPayouts_ReturnsEmptySuccessfulPage()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/payout-settlements");
        var body = await response.Content.ReadFromJsonAsync<PayoutsResponseContract>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.Items.Should().BeEmpty();
        body.TotalCount.Should().Be(0);
        body.TotalAmount.Should().Be(0m);
    }

    private sealed record PayoutsResponseContract(
        int PageNumber,
        int PageSize,
        int TotalCount,
        int TotalPages,
        decimal TotalAmount,
        IReadOnlyList<PayoutItemContract> Items);

    private sealed record PayoutItemContract(string PayoutId);
}
