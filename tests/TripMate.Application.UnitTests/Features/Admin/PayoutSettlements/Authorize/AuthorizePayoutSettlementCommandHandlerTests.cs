using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.Features.Admin.PayoutSettlements.Authorize;
using TripMate.Application.Features.Admin.PayoutSettlements.Common;
using TripMate.Application.UnitTests.Features.Operator.Payouts;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.PayoutSettlements.Authorize;

public sealed class AuthorizePayoutSettlementCommandHandlerTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUserService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly FakePayoutPersistenceErrorClassifier _classifier = new();
    private readonly AuthorizePayoutSettlementCommandHandler _handler;

    public AuthorizePayoutSettlementCommandHandlerTests()
    {
        _currentUserService.UserId = 2;
        _currentUserService.Role = "Administrator";
        _dateTimeProvider.UtcNow = NowUtc;
        _dbContext.Banks.Add(new Bank("970436", "Vietcombank", "Ngân hàng TMCP Ngoại Thương Việt Nam", NowUtc));
        _dbContext.Banks.Add(new Bank("970415", "VietinBank", "Ngân hàng TMCP Công thương Việt Nam", NowUtc));
        _dbContext.SaveChanges();
        _handler = new AuthorizePayoutSettlementCommandHandler(
            _dbContext, _currentUserService, _dateTimeProvider, _classifier);
    }

    [Fact]
    public async Task Handle_ApprovedPayout_MovesToProcessingAndQueuesExactlyOneWorkItemWithAudit()
    {
        var payout = await SeedApprovedAsync();

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PayoutId.Should().Be(payout.Id.ToString());
        result.Value.PayoutCode.Should().Be(payout.PayoutCode);
        result.Value.Status.Should().Be(Payout.StatusSettlementProcessing);
        result.Value.AuthorizedAtUtc.Should().Be(NowUtc);

        var stored = await _dbContext.Payouts.AsNoTracking().SingleAsync(item => item.Id == payout.Id);
        stored.Status.Should().Be(Payout.StatusSettlementProcessing);
        stored.SettlementAuthorizedBy.Should().Be(2);
        stored.SettlementAuthorizedAtUtc.Should().Be(NowUtc);
        stored.BankBin.Should().Be("970436");
        stored.ReviewedBy.Should().Be(5, "the Staff review is not overwritten");
        stored.GatewayAttemptCount.Should().Be(0, "authorizing never calls the gateway");

        var item = _dbContext.PayoutSettlementWorkItems.Should().ContainSingle().Subject;
        item.PayoutId.Should().Be(payout.Id);
        item.State.Should().Be(PayoutSettlementWorkItem.StatePending);
        item.NextAttemptAtUtc.Should().Be(NowUtc);

        var audit = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        audit.ActorUserId.Should().Be(2);
        audit.ActionType.Should().Be(AuditActionTypes.PayoutSettlementAuthorized);
        audit.AffectedEntity.Should().Be(AuditEntityTypes.Payout);
        audit.AffectedEntityId.Should().Be(payout.Id);
        audit.BeforeData.Should().Contain(Payout.StatusApprovedForSettlement);
        audit.AfterData.Should().Contain(Payout.StatusSettlementProcessing);
        (audit.BeforeData + audit.AfterData).Should().NotContain("1029384756").And.NotContain("protected:");
    }

    [Theory]
    [InlineData("Staff")]
    [InlineData("Traveler")]
    [InlineData("TourOperator")]
    [InlineData(null)]
    public async Task Handle_AnyoneButAnAdministrator_IsForbiddenAndWritesNothing(string? role)
    {
        _currentUserService.Role = role;
        _currentUserService.UserId = role is null ? null : 2;
        var payout = await SeedApprovedAsync();

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
        await AssertNothingWrittenAsync(payout.Id, Payout.StatusApprovedForSettlement);
    }

    [Fact]
    public async Task Handle_UnknownPayout_ReturnsNotFound()
    {
        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(999), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
        _dbContext.PayoutSettlementWorkItems.Should().BeEmpty();
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Payout.StatusPendingReview)]
    [InlineData(Payout.StatusSettlementProcessing)]
    [InlineData(Payout.StatusSettled)]
    [InlineData(Payout.StatusSettlementFailed)]
    [InlineData(Payout.StatusRejected)]
    public async Task Handle_PayoutNotAwaitingSettlement_ReturnsConflictWithCurrentStatusAndCreatesNoWork(string status)
    {
        var payout = await SeedInStatusAsync(status);

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutSettlementErrorCodes.NotApprovedForSettlement);
        result.ErrorMetadata["currentStatus"].Should().Be(status);
        _dbContext.PayoutSettlementWorkItems.Should().BeEmpty();
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_SecondAuthorization_IsAConflictAndKeepsASingleWorkItem()
    {
        var payout = await SeedApprovedAsync();
        (await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None)).IsSuccess.Should().BeTrue();

        var second = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        second.ErrorCode.Should().Be(PayoutSettlementErrorCodes.NotApprovedForSettlement);
        _dbContext.PayoutSettlementWorkItems.Should().ContainSingle();
        _dbContext.AuditLogs.Should().ContainSingle();
    }

    [Theory]
    [InlineData("bank")]
    [InlineData("holder")]
    [InlineData("account")]
    [InlineData("blankbank")]
    public async Task Handle_IncompleteBeneficiary_Returns422AndWritesNothing(string missing)
    {
        var payout = await SeedApprovedAsync();
        switch (missing)
        {
            case "bank":
                SetProperty(payout, nameof(Payout.BankName), null);
                break;
            case "holder":
                SetProperty(payout, nameof(Payout.AccountHolderName), null);
                break;
            case "account":
                SetProperty(payout, nameof(Payout.AccountNumberProtected), null);
                break;
            default:
                SetProperty(payout, nameof(Payout.BankName), "   ");
                break;
        }

        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutSettlementErrorCodes.BeneficiaryIncomplete);
        await AssertNothingWrittenAsync(payout.Id, Payout.StatusApprovedForSettlement);
    }

    [Theory]
    [InlineData(10_000_000.50)]
    [InlineData(0)]
    public async Task Handle_NetAmountNotAPositiveWholeNumber_Returns422AndWritesNothing(double paymentsReceived)
    {
        var payout = await SeedApprovedAsync(paymentsReceived: (decimal)paymentsReceived, commission: paymentsReceived == 0 ? 0m : 0.25m);

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutSettlementErrorCodes.AmountInvalid);
        await AssertNothingWrittenAsync(payout.Id, Payout.StatusApprovedForSettlement);
    }

    [Theory]
    [InlineData("Some Unlisted Bank")]
    [InlineData("Vietcom")]
    [InlineData("VCB")]
    public async Task Handle_BankNameWithoutExactCatalogueMatch_Returns422AndWritesNothing(string bankName)
    {
        var payout = await SeedApprovedAsync();
        SetProperty(payout, nameof(Payout.BankName), bankName);
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutSettlementErrorCodes.BankBinUnresolved);
        await AssertNothingWrittenAsync(payout.Id, Payout.StatusApprovedForSettlement);
    }

    [Fact]
    public async Task Handle_LegacyBankNameDifferingOnlyInCase_IsResolvedAndTheBinPersisted()
    {
        var payout = await SeedApprovedAsync();
        SetProperty(payout, nameof(Payout.BankName), "  VIETINBANK ");
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await _dbContext.Payouts.AsNoTracking().SingleAsync(item => item.Id == payout.Id)).BankBin.Should().Be("970415");
    }

    [Fact]
    public async Task Handle_StoredBinUnknownToTheCatalogue_Returns422EvenWhenTheNameWouldMatch()
    {
        var payout = await SeedApprovedAsync();
        SetProperty(payout, nameof(Payout.BankBin), "999999");
        await _dbContext.SaveChangesAsync();

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutSettlementErrorCodes.BankBinUnresolved);
        _dbContext.PayoutSettlementWorkItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTheSaveLosesAConcurrencyRace_ReturnsConflictAndPersistsNothing()
    {
        var payout = await SeedApprovedAsync();
        _dbContext.SaveChangesFailureFactory = () => new DbUpdateConcurrencyException("lost the race");

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutSettlementErrorCodes.NotApprovedForSettlement);
        result.ErrorMetadata.Should().ContainKey("currentStatus");
        _dbContext.SaveChangesFailureFactory = null;
        _dbContext.ChangeTracker.Clear();
        (await _dbContext.Payouts.AsNoTracking().SingleAsync(item => item.Id == payout.Id))
            .Status.Should().Be(Payout.StatusApprovedForSettlement);
        _dbContext.PayoutSettlementWorkItems.Should().BeEmpty();
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTheUniqueWorkItemIndexFires_ReturnsConflict()
    {
        var payout = await SeedApprovedAsync();
        _classifier.SettlementWorkItemConflict = true;
        _dbContext.SaveChangesFailureFactory = () => new DbUpdateException("duplicate work item");

        var result = await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(PayoutSettlementErrorCodes.NotApprovedForSettlement);
    }

    [Fact]
    public async Task Handle_WhenSavingFailsForAnyOtherReason_TheExceptionIsNotSwallowed()
    {
        var payout = await SeedApprovedAsync();
        _dbContext.SaveChangesFailureFactory = () => new DbUpdateException("connection dropped");

        var act = async () => await _handler.Handle(new AuthorizePayoutSettlementCommand(payout.Id), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private async Task AssertNothingWrittenAsync(long payoutId, string expectedStatus)
    {
        var stored = await _dbContext.Payouts.AsNoTracking().SingleAsync(item => item.Id == payoutId);
        stored.Status.Should().Be(expectedStatus);
        stored.SettlementAuthorizedBy.Should().BeNull();
        stored.SettlementAuthorizedAtUtc.Should().BeNull();
        _dbContext.PayoutSettlementWorkItems.Should().BeEmpty();
        _dbContext.AuditLogs.Should().BeEmpty();
    }

    private async Task<Payout> SeedApprovedAsync(decimal paymentsReceived = 10_000_000m, decimal commission = 1_000_000m)
    {
        var user = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        var payout = await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            _dbContext, user.Id, new DateOnly(2026, 9, 1), paymentsReceived, commission);
        payout.Approve(5, NowUtc.AddDays(-1));
        await _dbContext.SaveChangesAsync();
        return payout;
    }

    private async Task<Payout> SeedInStatusAsync(string status)
    {
        if (status is Payout.StatusPendingReview or Payout.StatusRejected)
        {
            var user = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
            var pending = await PayoutTestFixture.SeedPendingReviewPayoutAsync(_dbContext, user.Id, new DateOnly(2026, 9, 1));
            if (status == Payout.StatusRejected)
            {
                pending.Reject(5, "Mismatch", NowUtc);
                await _dbContext.SaveChangesAsync();
            }

            return pending;
        }

        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        return await GetPayoutSettlementsQueryTests.SeedAsync(
            _dbContext, operatorUser.Id, status, new DateOnly(2026, 9, 1), 10_000_000m);
    }

    private static void SetProperty(Payout payout, string property, string? value) =>
        typeof(Payout).GetProperty(property)!.SetValue(payout, value);
}
