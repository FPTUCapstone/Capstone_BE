using FluentAssertions;

using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Application.Features.Admin.Payouts.Reject;
using TripMate.Application.UnitTests.Features.Operator.Payouts;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts.Reject;

public sealed class RejectPayoutCommandHandlerTests
{
    private const string ValidReason = "Bank details could not be verified.";

    private static readonly DateTimeOffset NowUtc = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDbContext _dbContext = TestDbContext.Create();
    private readonly FakeCurrentUserService _currentUserService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly RejectPayoutCommandHandler _handler;

    public RejectPayoutCommandHandlerTests()
    {
        _currentUserService.UserId = 7;
        _currentUserService.Role = "Staff";
        _dateTimeProvider.UtcNow = NowUtc;
        _handler = new RejectPayoutCommandHandler(_dbContext, _currentUserService, _dateTimeProvider);
    }

    [Fact]
    public async Task Handle_WhenCallerIsNotStaff_ReturnsForbidden()
    {
        _currentUserService.Role = "Administrator";
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(new RejectPayoutCommand(payout.Id, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.Forbidden);
    }

    // SRS UC-74 only requires the reason be present — no minimum length (decided 2026-10-22,
    // scoped against the SRS text, which has no 10-character minimum and no MSG157). A short but
    // non-empty reason like "short" must succeed.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handle_WhenReasonIsEmptyAfterTrim_ReturnsRejectionReasonInvalid(string? reason)
    {
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(new RejectPayoutCommand(payout.Id, reason), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.RejectionReasonInvalid);
        var errors = result.ErrorMetadata["errors"].Should().BeOfType<Dictionary<string, string[]>>().Subject;
        errors.Should().ContainKey("reason");
    }

    [Fact]
    public async Task Handle_WhenReasonExceedsMaximumLength_ReturnsRejectionReasonInvalid()
    {
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(
            new RejectPayoutCommand(payout.Id, new string('a', 501)),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.RejectionReasonInvalid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public async Task Handle_WhenReasonIsAtLengthBoundary_Succeeds(int length)
    {
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(
            new RejectPayoutCommand(payout.Id, new string('a', length)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RejectionReason.Should().HaveLength(length);
    }

    [Fact]
    public async Task Handle_TrimsTheReasonBeforeValidatingAndStoring()
    {
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(
            new RejectPayoutCommand(payout.Id, $"  {ValidReason}  "),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RejectionReason.Should().Be(ValidReason);
    }

    [Fact]
    public async Task Handle_WhenPayoutDoesNotExist_ReturnsNotFound()
    {
        var result = await _handler.Handle(
            new RejectPayoutCommand(999, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_WhenPayoutIsPendingReview_RejectsAndWritesAuditRowWithReason()
    {
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(new RejectPayoutCommand(payout.Id, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(Payout.StatusRejected);
        result.Value.RejectionReason.Should().Be(ValidReason);

        var reloaded = await _dbContext.Payouts.FindAsync(payout.Id);
        reloaded!.Status.Should().Be(Payout.StatusRejected);
        reloaded.RejectionReason.Should().Be(ValidReason);
        reloaded.ReviewedBy.Should().Be(7);

        var auditRow = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        auditRow.Reason.Should().Be(ValidReason);
    }

    [Fact]
    public async Task Handle_WhenPayoutIsNotPendingReview_ReturnsConflictWithCurrentStatus()
    {
        var payout = await SeedPendingReviewAsync();
        var first = await _handler.Handle(new RejectPayoutCommand(payout.Id, ValidReason), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var result = await _handler.Handle(new RejectPayoutCommand(payout.Id, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotPendingReview);
        result.ErrorMetadata["currentStatus"].Should().Be(Payout.StatusRejected);
    }

    private async Task<Payout> SeedPendingReviewAsync()
    {
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(_dbContext);
        return await PayoutTestFixture.SeedPendingReviewPayoutAsync(
            _dbContext, operatorUser.Id, new DateOnly(2026, 9, 1));
    }
}