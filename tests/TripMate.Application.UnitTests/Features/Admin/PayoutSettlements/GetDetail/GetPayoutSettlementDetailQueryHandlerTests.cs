using System.Text.Json;

using FluentAssertions;

using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.Features.Admin.PayoutSettlements.GetDetail;
using TripMate.Application.UnitTests.Features.Operator.Payouts;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.PayoutSettlements.GetDetail;

public sealed class GetPayoutSettlementDetailQueryHandlerTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUserService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly GetPayoutSettlementDetailQueryHandler _handler;

    public GetPayoutSettlementDetailQueryHandlerTests()
    {
        _currentUserService.UserId = 2;
        _currentUserService.Role = "Administrator";
        _dateTimeProvider.UtcNow = NowUtc;
        _handler = new GetPayoutSettlementDetailQueryHandler(_dbContext, _currentUserService, _dateTimeProvider);
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Traveler")]
    [InlineData("TourOperator")]
    [InlineData(null)]
    public async Task Handle_AnyoneButAnAdministrator_IsForbiddenAndWritesNoAuditRow(string? role)
    {
        _currentUserService.Role = role;
        _currentUserService.UserId = role is null ? null : 2;
        var payout = await SeedApprovedAsync();

        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(payout.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_UnknownPayout_ReturnsNotFoundAndWritesNoAuditRow()
    {
        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(999), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("PendingReview")]
    [InlineData("Rejected")]
    public async Task Handle_PayoutOutsideTheSettlementScope_IsNotFound(string status)
    {
        var user = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(_dbContext, user.Id, new DateOnly(2026, 9, 1));
        if (status == "Rejected")
        {
            payout.Reject(5, "Mismatch", NowUtc);
            await _dbContext.SaveChangesAsync();
        }

        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ApprovedPayout_ReturnsSnapshotMaskedBeneficiaryAndAuditsTheView()
    {
        _dbContext.Banks.Add(new Bank("970436", "Vietcombank", "Ngân hàng TMCP Ngoại Thương Việt Nam", NowUtc));
        await _dbContext.SaveChangesAsync();
        var payout = await SeedApprovedAsync();

        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.PayoutId.Should().Be(payout.Id.ToString());
        detail.PayoutCode.Should().Be(payout.PayoutCode);
        detail.Status.Should().Be(Payout.StatusApprovedForSettlement);
        detail.GrossRevenue.Should().Be(payout.GrossRevenue);
        detail.CommissionAmount.Should().Be(payout.CommissionAmount);
        detail.NetPayoutAmount.Should().Be(payout.NetAmount);
        detail.Beneficiary.BankName.Should().Be("Vietcombank");
        detail.Beneficiary.AccountHolder.Should().Be("Nguyen Van A");
        detail.Beneficiary.MaskedAccountNumber.Should().Be("****1234");
        detail.BankBinResolved.Should().BeTrue();
        detail.ReviewedBy.Should().Be("5");
        detail.SettlementAuthorizedAtUtc.Should().BeNull();
        detail.Outcome.Should().BeNull();

        var audit = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        audit.ActorUserId.Should().Be(2);
        audit.ActionType.Should().Be(AuditActionTypes.PayoutSettlementDetailsViewed);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.Payout);
        audit.AffectedEntityId.Should().Be(payout.Id);
        (await _dbContext.Payouts.FindAsync(payout.Id))!.Status.Should().Be(Payout.StatusApprovedForSettlement);
    }

    [Theory]
    [InlineData("Some Unlisted Bank")]
    [InlineData("Vietcom")]
    public async Task Handle_BankNameWithoutAnExactCatalogueMatch_ReportsBinUnresolved(string bankName)
    {
        _dbContext.Banks.Add(new Bank("970436", "Vietcombank", "Ngân hàng TMCP Ngoại Thương Việt Nam", NowUtc));
        await _dbContext.SaveChangesAsync();
        var payout = await SeedApprovedAsync();
        SetBankName(payout, bankName);
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(payout.Id), CancellationToken.None);

        result.Value.BankBinResolved.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_PayoutBeingProcessed_ShowsSafeProcessingSummaryWithoutGatewayReference()
    {
        var payout = await SeedProcessingAsync(withReference: true);

        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(payout.Id), CancellationToken.None);

        var detail = result.Value;
        detail.Status.Should().Be(Payout.StatusSettlementProcessing);
        detail.SettlementAuthorizedBy.Should().Be("9");
        detail.SettlementAuthorizedAtUtc.Should().Be(NowUtc.AddHours(-1));
        detail.Outcome.Should().NotBeNull();
        detail.Outcome!.GatewayState.Should().Be(PayoutSettlementWorkItem.StateSubmitted);
        detail.Outcome.SubmittedAtUtc.Should().Be(NowUtc.AddMinutes(-50));
        detail.Outcome.FailureCode.Should().BeNull();
        detail.Outcome.ReconciliationRequired.Should().BeFalse();
        JsonSerializer.Serialize(detail).Should().NotContain("payos-secret-ref");
    }

    [Fact]
    public async Task Handle_FailedPayout_ShowsSafeFailureCode()
    {
        var payout = await SeedProcessingAsync(withReference: false);
        payout.RecordGatewayAttempt(NowUtc.AddMinutes(-40));
        payout.MarkSettlementFailed("gateway_rejected");
        var item = _dbContext.PayoutSettlementWorkItems.Single();
        var token = Guid.NewGuid();
        item.TryClaim(NowUtc.AddMinutes(-40), token, TimeSpan.FromMinutes(5));
        item.Complete(token, NowUtc.AddMinutes(-39));
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(payout.Id), CancellationToken.None);

        result.Value.Outcome!.FailureCode.Should().Be("gateway_rejected");
        result.Value.Outcome.GatewayState.Should().Be(PayoutSettlementWorkItem.StateCompleted);
    }

    [Fact]
    public async Task Handle_PayoutLeftForReconciliation_FlagsReconciliationRequired()
    {
        var payout = await SeedProcessingAsync(withReference: true);
        var item = _dbContext.PayoutSettlementWorkItems.Single();
        var token = Guid.NewGuid();
        item.TryClaim(NowUtc, token, TimeSpan.FromMinutes(5));
        item.RequireReconciliation(token, "poll_deadline_exceeded");
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(payout.Id), CancellationToken.None);

        result.Value.Status.Should().Be(Payout.StatusSettlementProcessing);
        result.Value.Outcome!.ReconciliationRequired.Should().BeTrue();
        result.Value.Outcome.GatewayState.Should().Be(PayoutSettlementWorkItem.StateManualReconciliation);
    }

    [Fact]
    public async Task Handle_Response_NeverCarriesAccountNumberOrGatewayReference()
    {
        var payout = await SeedProcessingAsync(withReference: true);
        payout.RecordGatewayAttempt(NowUtc);
        payout.MarkSettled();
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new GetPayoutSettlementDetailQuery(payout.Id), CancellationToken.None);

        var json = JsonSerializer.Serialize(result.Value);
        json.Should().NotContain("1029384756").And.NotContain("protected:")
            .And.NotContain("payos-secret-ref").And.NotContain("970436");
        json.ToLowerInvariant().Should().NotContain("accountnumberprotected").And.NotContain("gatewayreference");
    }

    private async Task<Payout> SeedApprovedAsync()
    {
        var user = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        return await GetPayoutSettlementsQueryTests.SeedAsync(
            _dbContext, user.Id, Payout.StatusApprovedForSettlement, new DateOnly(2026, 9, 1), 10_000_000m);
    }

    /// <summary>Authorized one hour ago; the work item was submitted fifty minutes ago.</summary>
    private async Task<Payout> SeedProcessingAsync(bool withReference)
    {
        var user = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            _dbContext, user.Id, new DateOnly(2026, 9, 1), 10_000_000m, 1_000_000m);
        payout.Approve(5, NowUtc.AddDays(-1));
        payout.AuthorizeSettlement(9, NowUtc.AddHours(-1), "970436");

        var item = PayoutSettlementWorkItem.Create(payout.Id, NowUtc.AddHours(-1));
        _dbContext.PayoutSettlementWorkItems.Add(item);
        if (withReference)
        {
            var token = Guid.NewGuid();
            item.TryClaim(NowUtc.AddMinutes(-55), token, TimeSpan.FromMinutes(5));
            payout.RecordGatewayAttempt(NowUtc.AddMinutes(-55));
            payout.SetGatewayReference("payos-secret-ref");
            item.RecordSubmitted(token, "payos-secret-ref", NowUtc.AddMinutes(-50), TimeSpan.FromHours(24), NowUtc.AddMinutes(-49));
        }

        await _dbContext.SaveChangesAsync();
        return payout;
    }

    private static void SetBankName(Payout payout, string bankName) =>
        typeof(Payout).GetProperty(nameof(Payout.BankName))!.SetValue(payout, bankName);
}
