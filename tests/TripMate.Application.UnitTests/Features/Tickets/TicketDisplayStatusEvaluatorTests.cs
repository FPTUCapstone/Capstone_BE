using FluentAssertions;

using TripMate.Application.Features.Tickets.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Tickets;

public class TicketDisplayStatusEvaluatorTests
{
    private static readonly TicketValidityWindow Window =
        TicketValidityWindow.For(new DateTimeOffset(2026, 10, 30, 1, 0, 0, TimeSpan.Zero));

    private static readonly DateTimeOffset BeforeWindow = Window.ValidFromUtc.AddTicks(-1);
    private static readonly DateTimeOffset InWindow = new(2026, 10, 30, 0, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterWindow = Window.ValidUntilUtc.AddTicks(1);

    [Theory]
    [InlineData(BookingStatus.Cancelled, TicketStatus.Valid)]
    [InlineData(BookingStatus.Cancelled, TicketStatus.Used)]
    [InlineData(BookingStatus.Confirmed, TicketStatus.Cancelled)]
    [InlineData(BookingStatus.Completed, TicketStatus.Cancelled)]
    public void Evaluate_CancelledBookingOrTicket_IsCancelledWhateverTheTime(
        BookingStatus bookingStatus, TicketStatus ticketStatus)
    {
        foreach (var now in new[] { BeforeWindow, InWindow, AfterWindow })
        {
            TicketDisplayStatusEvaluator.Evaluate(bookingStatus, ticketStatus, Window, now)
                .Should().Be(TicketDisplayStatus.Cancelled);
        }
    }

    [Fact]
    public void Evaluate_UsedTicket_IsUsedEvenAfterTheWindow()
    {
        foreach (var now in new[] { BeforeWindow, InWindow, AfterWindow })
        {
            TicketDisplayStatusEvaluator.Evaluate(BookingStatus.Completed, TicketStatus.Used, Window, now)
                .Should().Be(TicketDisplayStatus.Used);
        }
    }

    [Fact]
    public void Evaluate_StoredExpiredTicket_IsExpiredEvenInsideTheWindow()
    {
        TicketDisplayStatusEvaluator.Evaluate(BookingStatus.Confirmed, TicketStatus.Expired, Window, InWindow)
            .Should().Be(TicketDisplayStatus.Expired);
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Completed)]
    public void Evaluate_ValidTicket_FollowsTheWindow(BookingStatus bookingStatus)
    {
        TicketDisplayStatusEvaluator.Evaluate(bookingStatus, TicketStatus.Valid, Window, BeforeWindow)
            .Should().Be(TicketDisplayStatus.NotYetActive);
        TicketDisplayStatusEvaluator.Evaluate(bookingStatus, TicketStatus.Valid, Window, Window.ValidFromUtc)
            .Should().Be(TicketDisplayStatus.Valid);
        TicketDisplayStatusEvaluator.Evaluate(bookingStatus, TicketStatus.Valid, Window, InWindow)
            .Should().Be(TicketDisplayStatus.Valid);
        TicketDisplayStatusEvaluator.Evaluate(bookingStatus, TicketStatus.Valid, Window, Window.ValidUntilUtc)
            .Should().Be(TicketDisplayStatus.Valid);
        TicketDisplayStatusEvaluator.Evaluate(bookingStatus, TicketStatus.Valid, Window, AfterWindow)
            .Should().Be(TicketDisplayStatus.Expired);
    }

    [Theory]
    [InlineData(TicketDisplayStatus.NotYetActive, true)]
    [InlineData(TicketDisplayStatus.Valid, true)]
    [InlineData(TicketDisplayStatus.Used, false)]
    [InlineData(TicketDisplayStatus.Cancelled, false)]
    [InlineData(TicketDisplayStatus.Expired, false)]
    public void ShowsQrAndCanRefresh_OnlyForNotYetActiveAndValid(TicketDisplayStatus status, bool expected)
    {
        TicketDisplayStatusEvaluator.ShowsQr(status).Should().Be(expected);
        TicketDisplayStatusEvaluator.CanRefresh(status).Should().Be(expected);
    }
}
