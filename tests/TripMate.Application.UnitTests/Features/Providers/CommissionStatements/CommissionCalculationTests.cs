using FluentAssertions;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Providers.CommissionStatements;

public sealed class CommissionCalculationTests
{
    private static readonly DateTimeOffset Submitted = DateTimeOffset.Parse("2026-10-10T04:00:00Z");

    private static ServiceBooking CreateBooking(decimal totalPrice = 100000m)
    {
        return ServiceBooking.CreatePendingRequest(
            travelerUserId: 10,
            serviceId: 20,
            startAtUtc: DateTimeOffset.Parse("2026-10-15T02:00:00Z"),
            endAtUtc: null,
            quantity: 2,
            estimatedAmount: totalPrice,
            submittedAtUtc: Submitted);
    }

    [Theory]
    [InlineData(100000, 10000)]
    [InlineData(123456.78, 12345)]
    [InlineData(999999.99, 99999)]
    [InlineData(50, 5)]
    [InlineData(9, 0)]
    public void Completed_booking_calculates_ten_percent_floored_to_whole_vnd(decimal actualAmount, decimal expectedCommission)
    {
        var booking = CreateBooking();
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted).Should().BeTrue();

        var outcomeTime = DateTimeOffset.Parse("2026-10-15T17:00:00Z");
        var result = booking.TryRecordOutcome(ServiceBooking.Completed, actualAmount, 30, outcomeTime);

        result.Should().BeTrue();
        booking.ActualAmount.Should().Be(actualAmount);
        booking.CommissionAmount.Should().Be(expectedCommission);
    }

    [Theory]
    [InlineData(ServiceBooking.NoShow)]
    public void NoShow_booking_has_zero_commission(string outcome)
    {
        var booking = CreateBooking();
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted).Should().BeTrue();

        var outcomeTime = DateTimeOffset.Parse("2026-10-15T17:00:00Z");
        var result = booking.TryRecordOutcome(outcome, null, 30, outcomeTime);

        result.Should().BeTrue();
        booking.ActualAmount.Should().Be(0m);
        booking.CommissionAmount.Should().Be(0m);
    }

    [Fact]
    public void Rejected_booking_has_zero_commission()
    {
        var booking = CreateBooking();
        booking.TryDecide(ServiceBooking.Rejected, "Fully booked", 30, Submitted).Should().BeTrue();

        booking.ActualAmount.Should().BeNull();
        booking.CommissionAmount.Should().Be(0m);
    }

    [Fact]
    public void Cannot_record_outcome_when_statement_is_already_assigned()
    {
        var booking = CreateBooking();
        booking.TryDecide(ServiceBooking.Confirmed, null, 30, Submitted).Should().BeTrue();
        booking.TryRecordOutcome(ServiceBooking.Completed, 100000m, 30, DateTimeOffset.Parse("2026-10-15T17:00:00Z")).Should().BeTrue();

        // Assign statement
        booking.AssignCommissionStatement(99);
        booking.CommissionStatementId.Should().Be(99);

        // SRS §3.6.5 PC-02: Outcome is locked once monthly statement has been issued
        var modified = booking.TryRecordOutcome(ServiceBooking.Completed, 200000m, 30, DateTimeOffset.Parse("2026-10-16T17:00:00Z"));
        modified.Should().BeFalse();
        booking.ActualAmount.Should().Be(100000m);
    }
}
