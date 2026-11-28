using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.PostTripComplaints.Domain;

public sealed class StaffComplaintReviewTests
{
    [Fact]
    public void FinalDecline_RoutesToStaffWithoutCreatingRefund()
    {
        var complaint = PendingReview();
        complaint.Status.Should().Be(PostTripComplaintStatus.AwaitingStaffReview);
        complaint.RefundId.Should().BeNull();
    }

    [Fact]
    public void EvidenceResponse_ReturnsToReview_AndPreservesCommercialSnapshot()
    {
        var complaint = PendingReview();
        complaint.RequestStaffEvidence();
        complaint.Status.Should().Be(PostTripComplaintStatus.AwaitingStaffEvidence);
        complaint.ReceiveStaffEvidence();
        complaint.Status.Should().Be(PostTripComplaintStatus.AwaitingStaffReview);
        complaint.RefundableAmount.Should().Be(10000m);
        complaint.PartialRefundProposalRound.Should().Be(2);
    }

    [Fact]
    public void StaffFinalDecision_CannotBeRepeatedOrReopenNegotiation()
    {
        var complaint = PendingReview();
        complaint.ResolveByStaff(true);
        complaint.Status.Should().Be(PostTripComplaintStatus.Approved);
        Action repeat = () => complaint.ResolveByStaff(false);
        repeat.Should().Throw<InvalidOperationException>();
    }

    private static PostTripComplaint PendingReview()
    {
        var now = DateTimeOffset.UtcNow;
        var complaint = PostTripComplaint.Create(1, 2, PostTripComplaintReasonCategory.Other,
            "Service not supplied", now, now.AddDays(7), 10000);
        complaint.ProposePartialRefund(3, 2000, "First", now, now.AddDays(3));
        complaint.CounterPartialRefund(2, 8000, "Counter", now, now.AddDays(3));
        complaint.ProposePartialRefund(3, 3000, "Final", now, now.AddDays(3));
        complaint.DeclineFinalPartialRefund(2, "Not accepted");
        return complaint;
    }
}