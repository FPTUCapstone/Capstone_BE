using FluentAssertions;

using TripMate.Application.Features.Admin.Payouts.Approve;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts.Approve;

public sealed class ApprovePayoutCommandHandlerTests
{
    private static readonly DateTimeOffset RequestedAtUtc = new(2026, 10, 3, 8, 15, 0, TimeSpan.Zero);
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
    public async Task Handle_WhenPendingReview_ApprovesAndWritesAuditRow()
    {
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(Payout.StatusApprovedForSettlement);
        result.Value.DecidedAtUtc.Should().Be(NowUtc);

        var persisted = await _dbContext.Payouts.FindAsync(payout.Id);
        persisted!.Status.Should().Be(Payout.StatusApprovedForSettlement);
        persisted.ReviewedBy.Should().Be(7);
        persisted.ReviewedAtUtc.Should().Be(NowUtc);

        var auditRow = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        auditRow.ActorUserId.Should().Be(7);
        auditRow.AffectedEntityId.Should().Be(payout.Id);
    }

    [Fact]
    public async Task Handle_WhenAlreadyApproved_ReturnsConflictWithCurrentStatus()
    {
        var payout = await SeedPendingReviewAsync();
        await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);

        var second = await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);

        second.IsSuccess.Should().BeFalse();
        second.ErrorCode.Should().Be(PayoutErrorCodes.NotPendingReview);
        second.ErrorMetadata["currentStatus"].Should().Be(Payout.StatusApprovedForSettlement);

        // The first approval's reviewer and audit row must be the only ones recorded (I02 intent:
        // a loser must not silently re-approve or duplicate state).
        _dbContext.AuditLogs.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenAlreadyRejected_ReturnsConflictWithCurrentStatus()
    {
        var payout = await SeedPendingReviewAsync();
        payout.Reject(1, "Bank details could not be verified.", NowUtc.AddMinutes(-5));
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotPendingReview);
        result.ErrorMetadata["currentStatus"].Should().Be(Payout.StatusRejected);
    }

    [Fact]
    public async Task Handle_WhenConcurrentApprovalConflictOccurs_ReturnsConflictWithCommittedStatus()
    {
        var payout = await SeedPendingReviewAsync();
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // I02: simulates another request's Approve/Reject committing first between this
        // handler's load and its SaveChangesAsync.
        _dbContext.ThrowOnSaveConcurrency = true;

        var result = await _handler.Handle(new ApprovePayoutCommand(payout.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotPendingReview);
    }

    private async Task<Payout> SeedPendingReviewAsync()
    {
        var user = new User
        {
            FullName = "Hoi An Tours",
            Email = "operator@example.com",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        _dbContext.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            User = user,
            CompanyName = "Hoi An Tours",
            TaxCode = "TAX-001",
            BusinessLicenseNo = "LIC-001",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var payout = Payout.CreatePendingReview(
            user.Id,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 30),
            12_000_000m,
            1_200_000m,
            RequestedAtUtc);
        _dbContext.Payouts.Add(payout);
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return payout;
    }
}