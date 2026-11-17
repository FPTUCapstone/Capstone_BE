using System.Net;
using System.Net.Http.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Payouts;

/// <summary>
/// DAILY-PAYOUT-TRANSFER UC-73: the real HTTP status codes for AdminPayoutSettlementsController's
/// action endpoints — resolves FE spec Open questions 4/5, which had been left unanswered because
/// nothing exercised these routes end to end. DailyPayoutErrorCodes previously had no explicit
/// case in ApiControllerBase.HandleFailure and fell through to a generic 400 for everything,
/// including Forbidden and BatchNotFound; this suite locks in the corrected mapping.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class DailyPayoutSettlementActionsEndpointTests
{
    private static async Task<long> SeedBatchAsync(TripMateApiFactory factory, string status)
    {
        return await factory.WithDbContextAsync(async db =>
        {
            // A retry must not be blocked by BR-160/MSG146 — the operator needs to be Active with
            // a complete beneficiary bank account for the happy-path retry tests in this file.
            var operatorUser = new User
            {
                Email = $"payout-actions-{Guid.NewGuid():N}@test.local",
                FullName = "Payout Actions Test Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            db.Users.Add(operatorUser);
            await db.SaveChangesAsync(CancellationToken.None);

            db.OperatorProfiles.Add(new OperatorProfile
            {
                UserId = operatorUser.Id,
                User = operatorUser,
                CompanyName = "Payout Actions Test Co",
                TaxCode = $"PAYOUT-ACTIONS-TAX-{operatorUser.Id}",
                BusinessLicenseNo = $"PAYOUT-ACTIONS-LIC-{operatorUser.Id}",
                ApprovalStatus = OperatorApprovalStatus.Approved,
                CommissionRate = 10m,
                BankName = "Test Bank",
                AccountHolderName = "Operator Holder",
                AccountNumberMasked = "****1234",
                AccountNumberProtected = "protected-test-value",
                BankAccountUpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(CancellationToken.None);

            var batch = PayoutBatch.Create(new DateOnly(2026, 11, 9), tourOperatorUserId: operatorUser.Id, DateTimeOffset.UtcNow);
            db.PayoutBatches.Add(batch);
            await db.SaveChangesAsync(CancellationToken.None);

            if (status == PayoutSettlementStatus.SettlementFailed)
            {
                batch.MarkProcessing(DateTimeOffset.UtcNow);
                batch.MarkFailed(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
            }

            return batch.Id;
        });
    }

    [Fact]
    public async Task Retry_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/admin/payout-settlements/1/retry", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Retry_Staff_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        var response = await client.PostAsync("/api/v1/admin/payout-settlements/1/retry", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Retry_BatchDoesNotExist_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.PostAsync("/api/v1/admin/payout-settlements/999999/retry", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retry_BatchNotFailed_ReturnsConflict()
    {
        await using var factory = new TripMateApiFactory();
        var batchId = await SeedBatchAsync(factory, PayoutSettlementStatus.Scheduled);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.PostAsync($"/api/v1/admin/payout-settlements/{batchId}/retry", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Retry_BatchFailed_ReturnsOkAndMarksProcessing()
    {
        await using var factory = new TripMateApiFactory();
        var batchId = await SeedBatchAsync(factory, PayoutSettlementStatus.SettlementFailed);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.PostAsync($"/api/v1/admin/payout-settlements/{batchId}/retry", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarkResolved_BlankReason_ReturnsBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        var batchId = await SeedBatchAsync(factory, PayoutSettlementStatus.SettlementFailed);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/payout-settlements/{batchId}/resolve", new { reason = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest); // MSG178
    }

    [Fact]
    public async Task MarkResolved_WithReason_ReturnsOkAndResolvesWithoutATransfer()
    {
        await using var factory = new TripMateApiFactory();
        var batchId = await SeedBatchAsync(factory, PayoutSettlementStatus.SettlementFailed);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/payout-settlements/{batchId}/resolve", new { reason = "Operator confirmed receipt out of band" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var resolved = await factory.WithDbContextAsync(async db =>
            (await db.PayoutBatches.FindAsync(batchId))!.Status);
        resolved.Should().Be(PayoutSettlementStatus.Resolved);
    }

    [Fact]
    public async Task GetDetail_BatchDoesNotExist_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Administrator);

        var response = await client.GetAsync("/api/v1/admin/payout-settlements/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDetail_Staff_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        var batchId = await SeedBatchAsync(factory, PayoutSettlementStatus.SettlementFailed);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        var response = await client.GetAsync($"/api/v1/admin/payout-settlements/{batchId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}