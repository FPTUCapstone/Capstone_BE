using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Payouts;

[Collection(nameof(TripMateApiFactory))]
public sealed class PayoutSettlementEndpointTests
{
    private const string Route = "/api/v1/admin/payout-settlements";

    [Fact]
    public async Task Get_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Get_AnyRoleButAdministrator_ReturnsForbidden(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(3, role);

        var response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_AdministratorWithNoPayouts_ReturnsEmptySuccessfulPage()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var response = await client.GetAsync(Route);
        var body = await response.Content.ReadFromJsonAsync<PayoutsResponseContract>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.Items.Should().BeEmpty();
        body.TotalCount.Should().Be(0);
        body.TotalAmount.Should().Be(0m);
    }

    [Theory]
    [InlineData("ApprovedForSettlement")]
    [InlineData("SettlementProcessing")]
    [InlineData("Settled")]
    [InlineData("SettlementFailed")]
    public async Task Get_AdministratorWithSettlementScopeStatus_ReturnsOk(string status)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var response = await client.GetAsync($"{Route}?status={status}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("PendingReview")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    [InlineData("Bogus")]
    public async Task Get_StatusOutsideSettlementScope_ReturnsBadRequestWithStableErrorCode(string status)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var response = await client.GetAsync($"{Route}?status={status}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("payout.invalid_status");
    }

    [Fact]
    public async Task Get_InvertedPeriodRangeAndOversizedKeyword_ReturnStableBadRequests()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var period = await client.GetAsync($"{Route}?periodFrom=2026-09-10&periodTo=2026-09-01");
        var keyword = await client.GetAsync($"{Route}?keyword={new string('a', 201)}");

        period.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await period.Content.ReadAsStringAsync()).Should().Contain("payout.invalid_period_range");
        keyword.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await keyword.Content.ReadAsStringAsync()).Should().Contain("payout.invalid_filter");
    }

    [Fact]
    public async Task GetDetail_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Route}/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task GetDetail_AnyRoleButAdministrator_ReturnsForbidden(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(3, role);

        var response = await client.GetAsync($"{Route}/1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDetail_UnknownPayout_ReturnsNotFoundWithStableErrorCodeAndNoStoreCaching()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var response = await client.GetAsync($"{Route}/999");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().Contain("payout.not_found");
    }

    [Fact]
    public async Task GetDetail_SuccessResponse_IsNotCacheable()
    {
        await using var factory = new TripMateApiFactory();
        var payoutId = await SeedApprovedPayoutAsync(factory, withCatalogue: true);
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var response = await client.GetAsync($"{Route}/{payoutId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"ApprovedForSettlement\"").And.NotContain("1029384756");
    }

    [Fact]
    public async Task Authorize_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"{Route}/1/authorize", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Traveler)]
    [InlineData(UserRole.TourOperator)]
    public async Task Authorize_AnyRoleButAdministrator_ReturnsForbiddenAndLeavesThePayoutUntouched(UserRole role)
    {
        await using var factory = new TripMateApiFactory();
        var payoutId = await SeedApprovedPayoutAsync(factory, withCatalogue: true);
        using var client = factory.CreateAuthenticatedClient(3, role);

        var response = await client.PostAsync($"{Route}/{payoutId}/authorize", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadPayoutAsync(factory, payoutId)).Status.Should().Be(Payout.StatusApprovedForSettlement);
    }

    [Fact]
    public async Task Authorize_UnknownPayout_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var response = await client.PostAsync($"{Route}/999/authorize", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("payout.not_found");
    }

    [Fact]
    public async Task Authorize_Administrator_ReturnsAcceptedAndASecondCallConflicts()
    {
        await using var factory = new TripMateApiFactory();
        var payoutId = await SeedApprovedPayoutAsync(factory, withCatalogue: true);
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var first = await client.PostAsync($"{Route}/{payoutId}/authorize", content: null);
        var body = await first.Content.ReadAsStringAsync();
        var second = await client.PostAsync($"{Route}/{payoutId}/authorize", content: null);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        body.Should().Contain("\"status\":\"SettlementProcessing\"").And.Contain("\"payoutCode\":\"PO-INTEG-000001\"");
        body.Should().Contain("authorizedAtUtc");
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain("payout.not_approved_for_settlement");
        var stored = await ReadPayoutAsync(factory, payoutId);
        stored.Status.Should().Be(Payout.StatusSettlementProcessing);
        stored.BankBin.Should().Be("970436");
    }

    [Fact]
    public async Task Authorize_BankNotInTheCatalogue_ReturnsUnprocessableAndLeavesThePayoutUntouched()
    {
        await using var factory = new TripMateApiFactory();
        var payoutId = await SeedApprovedPayoutAsync(factory, withCatalogue: false);
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        var response = await client.PostAsync($"{Route}/{payoutId}/authorize", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("payout.bank_bin_unresolved");
        (await ReadPayoutAsync(factory, payoutId)).Status.Should().Be(Payout.StatusApprovedForSettlement);
    }

    /// <summary>The scope split must hold in the other direction as well: the Administrator
    /// settlement role does not unlock the Staff review endpoints.</summary>
    [Theory]
    [InlineData("GET", "/api/v1/admin/payouts")]
    [InlineData("GET", "/api/v1/admin/payouts/1")]
    [InlineData("POST", "/api/v1/admin/payouts/1/approve")]
    [InlineData("POST", "/api/v1/admin/payouts/1/reject")]
    public async Task StaffReviewEndpoints_AdministratorIsForbidden(string method, string route)
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { reason = "x" });
        }

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<Payout> ReadPayoutAsync(TripMateApiFactory factory, long payoutId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.Payouts.AsNoTracking().SingleAsync(item => item.Id == payoutId);
    }

    private static async Task<long> SeedApprovedPayoutAsync(TripMateApiFactory factory, bool withCatalogue)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var user = new User
        {
            Id = 7,
            Email = "operator7@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            User = user,
            CompanyName = "Company",
            TaxCode = "TAX-7",
            BusinessLicenseNo = "LIC-7",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CommissionRate = 10m,
        });
        var now = new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
        if (withCatalogue)
        {
            db.Banks.Add(new Bank("970436", "Vietcombank", "Ngân hàng TMCP Ngoại Thương Việt Nam", now));
        }

        var payout = Payout.Create(
            "PO-INTEG-000001",
            user.Id,
            [new PayoutMonthTotal(new DateOnly(2026, 9, 1), 1, 1, 10_000_000m, 0m, 0m, 1_000_000m, 9_000_000m)],
            now,
            0m,
            1_000_000m,
            "Vietcombank",
            "Nguyen Van A",
            "****1234",
            "protected:1029384756",
            now.AddDays(-3));
        payout.Approve(5, now.AddDays(-1));
        db.Payouts.Add(payout);
        await db.SaveChangesAsync(CancellationToken.None);
        return payout.Id;
    }

    private sealed record PayoutsResponseContract(
        int PageNumber,
        int PageSize,
        int TotalCount,
        int TotalPages,
        decimal TotalAmount,
        IReadOnlyList<object> Items);
}
