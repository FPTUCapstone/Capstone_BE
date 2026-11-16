using FluentAssertions;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.PostTripComplaints.Domain;

public sealed class PostTripComplaintTests
{
    [Fact]
    public void Create_ValidInput_InitializesAwaitingOperatorResponseAndImmutableSnapshot()
    {
        DateTimeOffset submittedAt = new(2026, 11, 15, 3, 0, 0, TimeSpan.Zero);

        PostTripComplaint complaint = PostTripComplaint.Create(100, 200,
            PostTripComplaintReasonCategory.ServiceQuality, "  Guide arrived late.  ",
            submittedAt, submittedAt.AddDays(7), 7000m);

        complaint.BookingId.Should().Be(100);
        complaint.TravelerUserId.Should().Be(200);
        complaint.Status.Should().Be(PostTripComplaintStatus.AwaitingOperatorResponse);
        complaint.ReasonDetail.Should().Be("Guide arrived late.");
        complaint.RefundableAmount.Should().Be(7000m);
        complaint.ResponseDeadlineUtc.Should().Be(submittedAt.AddDays(7));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-1)]
    public void Create_NegativeRefundableSnapshot_IsRejected(decimal refundableAmount)
    {
        Action action = () => PostTripComplaint.Create(100, 200,
            PostTripComplaintReasonCategory.Other, "Details", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7), refundableAmount);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_ZeroRefundableSnapshot_IsAllowed()
    {
        DateTimeOffset submittedAt = new(2026, 11, 15, 3, 0, 0, TimeSpan.Zero);
        PostTripComplaint complaint = PostTripComplaint.Create(100, 200,
            PostTripComplaintReasonCategory.Other, "Details", submittedAt,
            submittedAt.AddDays(7), 0m);

        complaint.RefundableAmount.Should().Be(0m);
    }

    [Fact]
    public void Create_BlankOrTooLongReason_IsRejected()
    {
        Action blank = () => PostTripComplaint.Create(100, 200,
            PostTripComplaintReasonCategory.Other, " ", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7), 0m);
        Action tooLong = () => PostTripComplaint.Create(100, 200,
            PostTripComplaintReasonCategory.Other,
            new string('x', PostTripComplaint.ReasonDetailMaxLength + 1), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7), 0m);

        blank.Should().Throw<ArgumentException>();
        tooLong.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SubmissionOperation_RequiresSha256FingerprintAndAssociatesComplaint()
    {
        DateTimeOffset now = new(2026, 11, 15, 3, 0, 0, TimeSpan.Zero);
        PostTripComplaint complaint = PostTripComplaint.Create(100, 200,
            PostTripComplaintReasonCategory.Other, "Details", now, now.AddDays(7), 0m);

        PostTripComplaintSubmissionOperation operation = PostTripComplaintSubmissionOperation.Create(200,
            Guid.NewGuid(), new string('A', PostTripComplaintSubmissionOperation.PayloadFingerprintLength), complaint, now);

        operation.TravelerUserId.Should().Be(200);
        operation.Complaint.Should().BeSameAs(complaint);
    }
}
