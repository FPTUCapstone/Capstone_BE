using FluentAssertions;

using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class PartialRefundProposalRulesTests
{
    private static readonly DateTimeOffset ProposedAt = new(2026, 11, 6, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeadlineFrom_AddsExactlySeventyTwoCalendarHours()
    {
        // BR-153 "3 days" is calendar time (G4): a Friday proposal closes on Monday, same time.
        PartialRefundProposalRules.DeadlineFrom(ProposedAt)
            .Should().Be(new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void DeadlineFrom_NormalizesToUtc()
    {
        var proposedInVietnam = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.FromHours(7));

        PartialRefundProposalRules.DeadlineFrom(proposedInVietnam).Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void IsOpen_BeforeDeadlineWhileAwaitingTraveler_IsTrue()
    {
        var deadline = PartialRefundProposalRules.DeadlineFrom(ProposedAt);

        PartialRefundProposalRules.IsOpen(PostTripComplaintStatus.AwaitingTravelerResponse, deadline,
            deadline.AddTicks(-1)).Should().BeTrue();
    }

    [Fact]
    public void IsOpen_ExactlyAtDeadline_IsFalse()
    {
        var deadline = PartialRefundProposalRules.DeadlineFrom(ProposedAt);

        PartialRefundProposalRules.IsOpen(PostTripComplaintStatus.AwaitingTravelerResponse, deadline, deadline)
            .Should().BeFalse();
    }

    [Fact]
    public void IsOpen_AfterDeadlineEvenIfJobHasNotRun_IsFalse()
    {
        // G5: the status is still AwaitingTravelerResponse because the hourly job has not run yet.
        var deadline = PartialRefundProposalRules.DeadlineFrom(ProposedAt);

        PartialRefundProposalRules.IsOpen(PostTripComplaintStatus.AwaitingTravelerResponse, deadline,
            deadline.AddMinutes(15)).Should().BeFalse();
    }

    [Theory]
    [InlineData(PostTripComplaintStatus.AwaitingOperatorResponse)]
    [InlineData(PostTripComplaintStatus.Approved)]
    [InlineData(PostTripComplaintStatus.Rejected)]
    [InlineData(PostTripComplaintStatus.Expired)]
    [InlineData(PostTripComplaintStatus.Declined)]
    public void IsOpen_WhenNotAwaitingTraveler_IsFalse(PostTripComplaintStatus status)
    {
        // G1: an already-answered proposal cannot be answered again.
        var deadline = PartialRefundProposalRules.DeadlineFrom(ProposedAt);

        PartialRefundProposalRules.IsOpen(status, deadline, ProposedAt.AddHours(1)).Should().BeFalse();
    }
}