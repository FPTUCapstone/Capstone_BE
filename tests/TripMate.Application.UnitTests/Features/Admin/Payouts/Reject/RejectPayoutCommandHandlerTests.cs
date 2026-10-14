using FluentAssertions;

using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Application.Features.Admin.Payouts.Reject;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Admin.Payouts.Reject;

public sealed class RejectPayoutCommandHandlerTests
{
    private const string ValidReason = "Bank details could not be verified.";

    private static readonly DateTimeOffset RequestedAtUtc = new(2026, 10, 3, 8, 15, 0, TimeSpan.Zero);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]       // 5 chars, below the minimum of 10
    public async Task Handle_WhenReasonIsBelowMinimumLength_ReturnsRejectionReasonInvalid(string? reason)
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
    [InlineData(10)]
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
            new RejectPayoutCommand(payout.Id, $"   {ValidReason}   "),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RejectionReason.Should().Be(ValidReason);
    }

    [Fact]
    public async Task Handle_WhenPayoutDoesNotExist_ReturnsNotFound()
    {
        var result = await _handler.Handle(new RejectPayoutCommand(999, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotFound);
    }

    [Fact]
    public async Task Handle_WhenPendingReview_RejectsAndWritesAuditRowWithReason()
    {
        var payout = await SeedPendingReviewAsync();

        var result = await _handler.Handle(new RejectPayoutCommand(payout.Id, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(Payout.StatusRejected);
        result.Value.RejectionReason.Should().Be(ValidReason);

        var persisted = await _dbContext.Payouts.FindAsync(payout.Id);
        persisted!.Status.Should().Be(Payout.StatusRejected);
        persisted.RejectionReason.Should().Be(ValidReason);
        persisted.ReviewedBy.Should().Be(7);

        var auditRow = _dbContext.AuditLogs.Should().ContainSingle().Subject;
        auditRow.Reason.Should().Be(ValidReason);
    }

    [Fact]
    public async Task Handle_WhenAlreadyRejected_ReturnsConflictAndDoesNotOverwriteTheFirstReason()
    {
        var payout = await SeedPendingReviewAsync();
        await _handler.Handle(new RejectPayoutCommand(payout.Id, ValidReason), CancellationToken.None);

        var second = await _handler.Handle(
            new RejectPayoutCommand(payout.Id, "A different reason given later."),
            CancellationToken.None);

        second.IsSuccess.Should().BeFalse();
        second.ErrorCode.Should().Be(PayoutErrorCodes.NotPendingReview);
        second.ErrorMetadata["currentStatus"].Should().Be(Payout.StatusRejected);

        var persisted = await _dbContext.Payouts.FindAsync(payout.Id);
        persisted!.RejectionReason.Should().Be(ValidReason);
    }

    [Fact]
    public async Task Handle_WhenAlreadyApproved_ReturnsConflictWithCurrentStatus()
    {
        var payout = await SeedPendingReviewAsync();
        payout.Approve(1, NowUtc.AddMinutes(-5));
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var result = await _handler.Handle(new RejectPayoutCommand(payout.Id, ValidReason), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(PayoutErrorCodes.NotPendingReview);
        result.ErrorMetadata["currentStatus"].Should().Be(Payout.StatusApprovedForSettlement);
    }

    [Fact]
    public async Task Handle_WhenConcurrentRejectionConflictOccurs_ReturnsConflictWithCommittedStatus()
    {
        var payout = await SeedPendingReviewAsync();
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // I02: simulates another request's Approve/Reject committing first between this
        // handler's load and its SaveChangesAsync.
        _dbContext.ThrowOnSaveConcurrency = true;

        var result = await _handler.Handle(new RejectPayoutCommand(payout.Id, ValidReason), CancellationToken.None);

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