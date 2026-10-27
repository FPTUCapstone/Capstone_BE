using FluentAssertions;

using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Common;

public sealed class BookingCancelEligibilityTests
{
    [Theory]
    [InlineData(BookingStatus.PendingPayment, false)]
    [InlineData(BookingStatus.Confirmed, false)]
    public void Evaluate_CancellableStatusWithoutUsedTicket_IsAllowed(BookingStatus status, bool hasUsedTicket)
    {
        var decision = BookingCancelEligibility.Evaluate(status, hasUsedTicket);

        decision.CanCancel.Should().BeTrue();
        decision.BlockedReason.Should().BeNull();
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment)]
    [InlineData(BookingStatus.Confirmed)]
    public void Evaluate_CancellableStatusWithUsedTicket_IsBlockedAsCheckedIn(BookingStatus status)
    {
        var decision = BookingCancelEligibility.Evaluate(status, hasUsedTicket: true);

        decision.CanCancel.Should().BeFalse();
        decision.BlockedReason.Should().Be(BookingCancelEligibility.BlockedCheckedIn);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled, false)]
    [InlineData(BookingStatus.Completed, false)]
    [InlineData(BookingStatus.Expired, false)]
    [InlineData(BookingStatus.Cancelled, true)]
    [InlineData(BookingStatus.Completed, true)]
    [InlineData(BookingStatus.Expired, true)]
    public void Evaluate_TerminalStatus_IsBlockedAsInvalidStatusEvenWithUsedTicket(
        BookingStatus status, bool hasUsedTicket)
    {
        var decision = BookingCancelEligibility.Evaluate(status, hasUsedTicket);

        decision.CanCancel.Should().BeFalse();
        decision.BlockedReason.Should().Be(BookingCancelEligibility.BlockedInvalidStatus);
    }

    [Fact]
    public void BlockedReasonConstants_MatchTheContractValues()
    {
        BookingCancelEligibility.BlockedInvalidStatus.Should().Be("InvalidStatus");
        BookingCancelEligibility.BlockedCheckedIn.Should().Be("CheckedIn");
    }
}
