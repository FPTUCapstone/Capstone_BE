using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutsEndpointTests
{
    [Fact]
    public async Task Get_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/payouts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Administrator_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/payouts");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_StaffWithNoPayouts_ReturnsEmptySuccessfulPage()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        var response = await client.GetAsync("/api/v1/admin/payouts");
        var body = await response.Content.ReadFromJsonAsync<PayoutsResponseContract>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.Items.Should().BeEmpty();
        body.TotalCount.Should().Be(0);
        body.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Get_InvertedPeriodRange_ReturnsBadRequestWithProposedMsg134()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        var response = await client.GetAsync("/api/v1/admin/payouts?periodFrom=2026-09-10&periodTo=2026-09-01");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("payout.invalid_period_range");
        body.Should().Contain("The submitted settlement period range is logically invalid.");
    }

    [Fact]
    public async Task Get_InvalidStatus_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        var response = await client.GetAsync("/api/v1/admin/payouts?status=SettlementProcessing");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record PayoutsResponseContract(
        int PageNumber,
        int PageSize,
        int TotalCount,
        int TotalPages,
        decimal TotalAmount,
        IReadOnlyList<PayoutItemContract> Items);

    private sealed record PayoutItemContract(
        string PayoutId,
        string PayoutCode,
        PayoutOperatorContract Operator,
        string PeriodStart,
        string PeriodEnd,
        decimal GrossRevenue,
        decimal CommissionAmount,
        decimal NetAmount,
        string? RequestedAtUtc,
        string Status);

    private sealed record PayoutOperatorContract(string UserId, string CompanyName);
}
