using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.ServiceProviderRequests;

public sealed class ServiceBookingLifecycleTests
{
    private static readonly DateTimeOffset Submitted = DateTimeOffset.Parse("2026-11-10T04:00:00Z");

    [Fact]
    public void Rejection_requires_reason_and_decision_is_final()
    {
        var booking = Request();
        booking.TryDecide(ServiceBooking.Rejected, " ", 30, Submitted).Should().BeFalse();
        booking.TryDecide(ServiceBooking.Rejected, " Fully booked ", 30, Submitted).Should().BeTrue();
        booking.RejectionReason.Should().Be("Fully booked");
        booking.RespondedByUserId.Should().Be(30);
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted).Should().BeFalse();
        booking.CommissionAmount.Should().Be(0);
    }

    [Fact]
    public void Outcome_opens_at_next_Vietnam_midnight_and_floors_commission()
    {
        var booking = Request();
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted).Should().BeTrue();
        booking.TryRecordOutcome(ServiceBooking.Completed, 123456.78m, 30,
            DateTimeOffset.Parse("2026-11-12T16:59:59Z")).Should().BeFalse();
        booking.TryRecordOutcome(ServiceBooking.Completed, 123456.78m, 30,
            DateTimeOffset.Parse("2026-11-12T17:00:00Z")).Should().BeTrue();
        booking.CommissionAmount.Should().Be(12345m);
        booking.TotalPrice.Should().Be(234567.89m);
        booking.TryRecordOutcome(ServiceBooking.NoShow, null, 30,
            DateTimeOffset.Parse("2026-11-13T17:00:00Z")).Should().BeFalse();
    }

    [Fact]
    public void Automatic_completion_uses_three_complete_Vietnam_days_after_requested_date()
    {
        var booking = Request();
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted).Should().BeTrue();
        // Requested date is November 12. November 13, 14 and 15 are the three response days.
        booking.TryDefaultComplete(DateTimeOffset.Parse("2026-11-15T16:59:59Z")).Should().BeFalse();
        booking.TryDefaultComplete(DateTimeOffset.Parse("2026-11-15T17:00:00Z")).Should().BeTrue();
        booking.ActualAmount.Should().Be(234567.89m);
        booking.CommissionAmount.Should().Be(23456m);
        booking.IsAutoCompleted.Should().BeTrue();
    }

    [Theory]
    [InlineData(ServiceBooking.Completed, 100000)]
    [InlineData(ServiceBooking.NoShow, 0)]
    public void Manual_outcomes_close_when_automatic_completion_becomes_due(string outcome, decimal amount)
    {
        var booking = Request();
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted).Should().BeTrue();
        booking.TryRecordOutcome(outcome, amount, 30, booking.AutoCompleteAtUtc).Should().BeFalse();
        booking.Status.Should().Be(ServiceBooking.Confirmed);
        booking.TryDefaultComplete(booking.AutoCompleteAtUtc).Should().BeTrue();
        booking.IsAutoCompleted.Should().BeTrue();
        booking.ActualAmount.Should().Be(booking.TotalPrice);
    }

    [Fact]
    public void Legacy_confirmed_booking_is_never_processed_by_lifecycle()
    {
        var booking = new ServiceBooking();
        typeof(ServiceBooking).GetProperty(nameof(ServiceBooking.Status))!.SetValue(booking, "Confirmed");
        booking.TryDefaultComplete(Submitted.AddYears(1)).Should().BeFalse();
        booking.TryRecordOutcome(ServiceBooking.NoShow, null, 30, Submitted.AddYears(1)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000000000)]
    public void Manual_completion_requires_positive_amount_within_database_precision(decimal amount)
    {
        var booking = Request();
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted);
        booking.TryRecordOutcome(ServiceBooking.Completed, amount, 30, Submitted.AddDays(3)).Should().BeFalse();
    }

    [Fact]
    public void No_show_carries_zero_commission()
    {
        var booking = Request();
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted);
        booking.TryRecordOutcome(ServiceBooking.NoShow, null, 30, Submitted.AddDays(3)).Should().BeTrue();
        booking.CommissionAmount.Should().Be(0);
        booking.ActualAmount.Should().Be(0);
    }

    private static ServiceBooking Request() => ServiceBooking.CreatePendingRequest(10, 20,
        DateTimeOffset.Parse("2026-11-12T15:00:00Z"), null, 2, 234567.89m, Submitted);

    [Fact]
    public void Decision_at_exact_response_deadline_is_refused_and_request_can_expire()
    {
        var booking = ServiceBooking.CreatePendingRequest(10, 20,
            DateTimeOffset.Parse("2026-11-12T15:00:00Z"), null, 2, 123456.78m, Submitted);

        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted.AddHours(24)).Should().BeFalse();
        booking.TryExpire(Submitted.AddHours(24)).Should().BeTrue();
        booking.Status.Should().Be(ServiceBooking.Expired);
        booking.CommissionAmount.Should().Be(0);
        booking.TryExpire(Submitted.AddHours(25)).Should().BeFalse();
    }
}