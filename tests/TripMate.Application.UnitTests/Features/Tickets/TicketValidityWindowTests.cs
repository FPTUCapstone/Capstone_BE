using FluentAssertions;

using TripMate.Application.Features.Tickets.Common;

namespace TripMate.Application.UnitTests.Features.Tickets;

public class TicketValidityWindowTests
{
    [Fact]
    public void For_ADepartureMorning_CoversTheWholeVietnamCalendarDay()
    {
        var window = TicketValidityWindow.For(new DateTimeOffset(2026, 10, 30, 1, 0, 0, TimeSpan.Zero));

        window.DepartureDate.Should().Be(new DateOnly(2026, 10, 30));
        window.ValidFromUtc.Should().Be(new DateTimeOffset(2026, 10, 29, 17, 0, 0, TimeSpan.Zero));
        window.ValidUntilUtc.Should().Be(
            new DateTimeOffset(2026, 10, 30, 17, 0, 0, TimeSpan.Zero).AddTicks(-1));
        window.ValidFromUtc.Offset.Should().Be(TimeSpan.Zero);
        window.ValidUntilUtc.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void For_StartsEitherSideOfVietnamMidnight_FallOnDifferentDates()
    {
        var beforeMidnight = TicketValidityWindow.For(new DateTimeOffset(2026, 10, 29, 16, 59, 0, TimeSpan.Zero));
        var atMidnight = TicketValidityWindow.For(new DateTimeOffset(2026, 10, 29, 17, 0, 0, TimeSpan.Zero));

        beforeMidnight.DepartureDate.Should().Be(new DateOnly(2026, 10, 29));
        atMidnight.DepartureDate.Should().Be(new DateOnly(2026, 10, 30));
    }

    [Fact]
    public void For_AnInputWithAnotherOffset_UsesTheSameInstant()
    {
        var utc = TicketValidityWindow.For(new DateTimeOffset(2026, 10, 30, 1, 0, 0, TimeSpan.Zero));
        var local = TicketValidityWindow.For(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.FromHours(7)));

        local.Should().Be(utc);
    }

    [Fact]
    public void TimeZone_IsTheContractValue()
    {
        TicketValidityWindow.TimeZoneId.Should().Be("Asia/Ho_Chi_Minh");
    }
}
