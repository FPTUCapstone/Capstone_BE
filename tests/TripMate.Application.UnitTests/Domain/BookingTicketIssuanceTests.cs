using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class BookingTicketIssuanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IssueTicket_ForAConfirmedPaidBooking_IssuesOneValidTicket()
    {
        var booking = Restore(456, BookingStatus.Confirmed, PaymentStatus.Paid);

        var issued = booking.IssueTicket(Now);

        issued.Should().BeTrue();
        var ticket = booking.Tickets.Should().ContainSingle().Subject;
        ticket.BookingId.Should().Be(456);
        ticket.Booking.Should().BeSameAs(booking);
        ticket.TicketCode.Should().Be("TK-0000000456");
        ticket.Status.Should().Be(TicketStatus.Valid);
        ticket.IssuedAtUtc.Should().Be(Now);
        ticket.ParticipantId.Should().BeNull();
        ticket.ExpiresAtUtc.Should().BeNull();
        ticket.CheckedInAtUtc.Should().BeNull();
    }

    [Fact]
    public void IssueTicket_CalledTwice_IssuesOnlyOneTicket()
    {
        var booking = Restore(456, BookingStatus.Confirmed, PaymentStatus.Paid);
        booking.IssueTicket(Now);

        var issuedAgain = booking.IssueTicket(Now.AddMinutes(1));

        issuedAgain.Should().BeFalse();
        booking.Tickets.Should().ContainSingle();
    }

    [Theory]
    [InlineData(BookingStatus.PendingPayment, PaymentStatus.Unpaid)]
    [InlineData(BookingStatus.Expired, PaymentStatus.Unpaid)]
    [InlineData(BookingStatus.Cancelled, PaymentStatus.Refunded)]
    [InlineData(BookingStatus.Confirmed, PaymentStatus.Unpaid)]
    [InlineData(BookingStatus.Completed, PaymentStatus.Paid)]
    public void IssueTicket_UnlessConfirmedAndPaid_IssuesNothing(BookingStatus status, PaymentStatus paymentStatus)
    {
        var booking = Restore(456, status, paymentStatus);

        var issued = booking.IssueTicket(Now);

        issued.Should().BeFalse();
        booking.Tickets.Should().BeEmpty();
    }

    [Fact]
    public void IssueTicket_ForABookingNotYetPersisted_Throws()
    {
        var booking = Booking.Create(travelerUserId: 10, tourScheduleId: 20, quantity: 1, totalAmount: 500_000m,
            BookingStatus.Confirmed, PaymentStatus.Paid);

        var action = () => booking.IssueTicket(Now);

        action.Should().Throw<InvalidOperationException>();
        booking.Tickets.Should().BeEmpty();
    }

    private static Booking Restore(long id, BookingStatus status, PaymentStatus paymentStatus) =>
        Booking.Restore(id, travelerUserId: 10, tourScheduleId: 20, quantity: 2, totalAmount: 1_000_000m,
            status, paymentStatus, Booking.DefaultCancellationPolicySnapshot);
}