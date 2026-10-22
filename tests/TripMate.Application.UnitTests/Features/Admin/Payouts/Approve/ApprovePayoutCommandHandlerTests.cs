using FluentAssertions;

using TripMate.Application.Features.Admin.Payouts.Approve;
using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.UnitTests.Features.Operator.Payouts;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts.Approve;

public sealed class ApprovePayoutCommandHandlerTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUserService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly ApprovePayoutCommandHandler _handler;

    public ApprovePayoutCommandHandlerTests()
    {
        _currentUserService.UserId = 7;
        _currentUserService.Role = "Staff";
        _dateTimeProvider.UtcNow = NowUtc;
        _handler = new ApprovePayoutCommandHandler(_dbContext, _currentUserService, _dateTimeProvider);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotStaff_ReturnsForbidden()
    {
        _currentUserService.Role = "Administrator";
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Handle_WhenPayoutDoesNotExist_ReturnsNotFound()
    {
        var result = await _handler.Handle(new ApprovePayoutCommand(999), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_WhenPayoutIsPendingReview_ApprovesAndWritesAuditRow()
    {
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(Payout.StatusApprovedForSettlement);
        result.Value.DecidedAtUtc.Should().Be(NowUtc);

        var reloaded = await _dbContext.Payouts.FindAsync(payout.Id);
        reloaded!.Status.Should().Be(Payout.StatusApprovedForSettlement);
        reloaded.ReviewedBy.Should().Be(7);
        reloaded.ReviewedAtUtc.Should().Be(NowUtc);

        var auditRow = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        auditRow.ActorUserId.Should().Be(7);
        auditRow.AffectedEntityId.Should().Be(payout.Id);
    }

    [Fact]
    public async Task Handle_WhenPayoutIsNotPendingReview_ReturnsConflictWithCurrentStatus()
    {
        var payout = await SeedPendingReviewAsync();
        var firstApproval = await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);
        firstApproval.IsSuccess.Should().BeTrue();

        var result = await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotPendingReview);
        result.ErrorMetadata["currentStatus"].Should().Be(Payout.StatusApprovedForSettlement);
    }

    private async Task<Payout> SeedPendingReviewAsync()
    {
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        return await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            _dbContext, operatorUser.Id, new DateOnly(2026, 9, 1));
    }
}
