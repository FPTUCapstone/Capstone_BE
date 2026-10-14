using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class PayoutTests
{
    private static readonly DateTimeOffset RequestedAtUtc = new(2026, 10, 3, 8, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReviewedAtUtc = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Approve_FromPendingReview_TransitionsAndRecordsReviewer()
    {
        var payout = CreatePendingReview();

        var transitioned = payout.Approve(reviewerUserId: 42, ReviewedAtUtc);

        transitioned.Should().BeTrue();
        payout.Status.Should().Be(Payout.StatusApprovedForSettlement);
        payout.ReviewedBy.Should().Be(42);
        payout.ReviewedAtUtc.Should().Be(ReviewedAtUtc);
        payout.RejectionReason.Should().BeNull();
    }

    [Fact]
    public void Approve_WhenAlreadyApproved_ReturnsFalseAndLeavesStateUnchanged()
    {
        var payout = CreatePendingReview();
        payout.Approve(1, ReviewedAtUtc.AddMinutes(-10)).Should().BeTrue();

        var transitioned = payout.Approve(reviewerUserId: 99, ReviewedAtUtc);

        transitioned.Should().BeFalse();
        payout.Status.Should().Be(Payout.StatusApprovedForSettlement);
        payout.ReviewedBy.Should().Be(1, "the second Approve call must not overwrite the first reviewer");
    }

    [Fact]
    public void Approve_WhenAlreadyRejected_ReturnsFalseAndLeavesStateUnchanged()
    {
        var payout = CreatePendingReview();
        payout.Reject(1, "Bank details could not be verified.", ReviewedAtUtc.AddMinutes(-10)).Should().BeTrue();

        var transitioned = payout.Approve(reviewerUserId: 99, ReviewedAtUtc);

        transitioned.Should().BeFalse();
        payout.Status.Should().Be(Payout.StatusRejected);
    }

    [Fact]
    public void Approve_WithNonPositiveReviewerId_Throws()
    {
        var payout = CreatePendingReview();

        var act = () => payout.Approve(reviewerUserId: 0, ReviewedAtUtc);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reject_FromPendingReview_TransitionsAndStoresReason()
    {
        var payout = CreatePendingReview();

        var transitioned = payout.Reject(reviewerUserId: 42, "Bank details could not be verified.", ReviewedAtUtc);

        transitioned.Should().BeTrue();
        payout.Status.Should().Be(Payout.StatusRejected);
        payout.ReviewedBy.Should().Be(42);
        payout.ReviewedAtUtc.Should().Be(ReviewedAtUtc);
        payout.RejectionReason.Should().Be("Bank details could not be verified.");
    }

    [Fact]
    public void Reject_WhenAlreadyApproved_ReturnsFalseAndLeavesStateUnchanged()
    {
        var payout = CreatePendingReview();
        payout.Approve(1, ReviewedAtUtc.AddMinutes(-10)).Should().BeTrue();

        var transitioned = payout.Reject(reviewerUserId: 42, "Bank details could not be verified.", ReviewedAtUtc);

        transitioned.Should().BeFalse();
        payout.Status.Should().Be(Payout.StatusApprovedForSettlement);
        payout.RejectionReason.Should().BeNull();
    }

    [Theory]
    [InlineData(9)]   // one under the minimum
    [InlineData(501)] // one over the maximum
    public void Reject_WithReasonOutsideLengthBounds_Throws(int length)
    {
        var payout = CreatePendingReview();

        var act = () => payout.Reject(reviewerUserId: 42, new string('a', length), ReviewedAtUtc);

        act.Should().Throw<ArgumentException>();
        payout.Status.Should().Be(Payout.StatusPendingReview, "a thrown precondition must not leave a half-applied transition");
    }

    [Theory]
    [InlineData(10)]  // exact minimum
    [InlineData(500)] // exact maximum
    public void Reject_WithReasonAtLengthBoundaries_Succeeds(int length)
    {
        var payout = CreatePendingReview();

        var transitioned = payout.Reject(reviewerUserId: 42, new string('a', length), ReviewedAtUtc);

        transitioned.Should().BeTrue();
        payout.RejectionReason.Should().HaveLength(length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reject_WithNullOrWhitespaceReason_Throws(string? reason)
    {
        var payout = CreatePendingReview();

        var act = () => payout.Reject(reviewerUserId: 42, reason!, ReviewedAtUtc);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reject_WithNonPositiveReviewerId_Throws()
    {
        var payout = CreatePendingReview();

        var act = () => payout.Reject(reviewerUserId: -1, "Bank details could not be verified.", ReviewedAtUtc);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreatePendingReview_ComputesNetAmountAsGrossMinusCommission()
    {
        var payout = Payout.CreatePendingReview(
            operatorUserId: 10,
            periodStart: new DateOnly(2026, 9, 1),
            periodEnd: new DateOnly(2026, 9, 30),
            grossRevenue: 12_000_000m,
            commissionAmount: 1_200_000m,
            RequestedAtUtc);

        payout.NetAmount.Should().Be(10_800_000m);
        payout.Status.Should().Be(Payout.StatusPendingReview);
        payout.ReviewedBy.Should().BeNull();
        payout.RejectionReason.Should().BeNull();
    }

    [Fact]
    public void CreatePendingReview_WithPeriodEndBeforeStart_Throws()
    {
        var act = () => Payout.CreatePendingReview(
            operatorUserId: 10,
            periodStart: new DateOnly(2026, 9, 30),
            periodEnd: new DateOnly(2026, 9, 1),
            grossRevenue: 100m,
            commissionAmount: 10m,
            RequestedAtUtc);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreatePendingReview_WithCommissionExceedingGross_Throws()
    {
        var act = () => Payout.CreatePendingReview(
            operatorUserId: 10,
            periodStart: new DateOnly(2026, 9, 1),
            periodEnd: new DateOnly(2026, 9, 30),
            grossRevenue: 100m,
            commissionAmount: 101m,
            RequestedAtUtc);

        act.Should().Throw<ArgumentException>();
    }

    private static Payout CreatePendingReview() =>
        Payout.CreatePendingReview(
            operatorUserId: 10,
            periodStart: new DateOnly(2026, 9, 1),
            periodEnd: new DateOnly(2026, 9, 30),
            grossRevenue: 12_000_000m,
            commissionAmount: 1_200_000m,
            RequestedAtUtc);
}