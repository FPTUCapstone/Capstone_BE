using FluentAssertions;

using TripMate.Application.Features.Operator.Bookings.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Bookings.Common;

public sealed class BookingCheckInTests
{
    private static readonly DateTimeOffset First = new(2026, 11, 2, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Second = new(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Resolve_NoTickets_IsNotCheckedIn()
    {
        var result = BookingCheckIn.Resolve([]);

        result.Status.Should().Be(BookingCheckIn.NotCheckedIn);
        result.CheckedInAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(TicketStatus.Valid)]
    [InlineData(TicketStatus.Cancelled)]
    [InlineData(TicketStatus.Expired)]
    public void Resolve_UnusedTicketWithoutTimestamp_IsNotCheckedIn(TicketStatus status)
    {
        var result = BookingCheckIn.Resolve([new TicketCheckInSnapshot(status, null)]);

        result.Status.Should().Be(BookingCheckIn.NotCheckedIn);
        result.CheckedInAtUtc.Should().BeNull();
    }

    [Fact]
    public void Resolve_UsedTicketWithoutTimestamp_IsCheckedInWithNullTimestamp()
    {
        var result = BookingCheckIn.Resolve([new TicketCheckInSnapshot(TicketStatus.Used, null)]);

        result.Status.Should().Be(BookingCheckIn.CheckedIn);
        result.CheckedInAtUtc.Should().BeNull();
    }

    [Fact]
    public void Resolve_UsedTicketWithTimestamp_ReturnsTimestamp()
    {
        var result = BookingCheckIn.Resolve([new TicketCheckInSnapshot(TicketStatus.Used, First)]);

        result.Status.Should().Be(BookingCheckIn.CheckedIn);
        result.CheckedInAtUtc.Should().Be(First);
    }

    [Theory]
    [InlineData(TicketStatus.Valid)]
    [InlineData(TicketStatus.Cancelled)]
    public void Resolve_NonUsedTicketWithTimestamp_IsCheckedIn(TicketStatus status)
    {
        var result = BookingCheckIn.Resolve([new TicketCheckInSnapshot(status, First)]);

        result.Status.Should().Be(BookingCheckIn.CheckedIn);
        result.CheckedInAtUtc.Should().Be(First);
    }

    [Fact]
    public void Resolve_SeveralTickets_ReturnsEarliestNonNullTimestamp()
    {
        var result = BookingCheckIn.Resolve(
        [
            new TicketCheckInSnapshot(TicketStatus.Valid, null),
            new TicketCheckInSnapshot(TicketStatus.Used, Second),
            new TicketCheckInSnapshot(TicketStatus.Used, First),
        ]);

        result.Status.Should().Be(BookingCheckIn.CheckedIn);
        result.CheckedInAtUtc.Should().Be(First);
    }

    [Fact]
    public void StatusConstants_MatchTheContractValues()
    {
        BookingCheckIn.CheckedIn.Should().Be("CheckedIn");
        BookingCheckIn.NotCheckedIn.Should().Be("NotCheckedIn");
    }
}
