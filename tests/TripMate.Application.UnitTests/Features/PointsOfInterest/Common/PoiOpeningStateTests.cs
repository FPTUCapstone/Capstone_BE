using FluentAssertions;

using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Common;

public class PoiOpeningStateTests
{
    // Vietnam fixed offset UTC+07:00
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    // --- IsOpenNow: normal open interval ---

    [Fact]
    public void IsOpenNow_WithinOpenHours_ReturnsTrue()
    {
        // Wednesday 2026-09-09 at 10:00 Vietnam time → 03:00 UTC, day=3 (Wednesday)
        var utcNow = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);
        var openingHours = new[] { MakeOpenHour(3, new TimeOnly(8, 0), new TimeOnly(17, 0)) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeTrue();
    }

    // --- IsOpenNow: exact opening time boundary ---

    [Fact]
    public void IsOpenNow_AtExactOpeningTime_ReturnsTrue()
    {
        // 2026-09-08 is Tuesday day=2, openTime=08:00 Vietnam → 01:00 UTC
        var utcNow = new DateTimeOffset(2026, 9, 8, 1, 0, 0, TimeSpan.Zero);
        var openingHours = new[] { MakeOpenHour(2, new TimeOnly(8, 0), new TimeOnly(17, 0)) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeTrue();
    }

    // --- IsOpenNow: one tick before closing ---

    [Fact]
    public void IsOpenNow_OneTickBeforeClosing_ReturnsTrue()
    {
        // 2026-09-08 is Tuesday day=2, closeTime=17:00 Vietnam → 10:00 UTC, one tick before
        var utcNow = new DateTimeOffset(2026, 9, 8, 9, 59, 59, TimeSpan.Zero)
            .AddTicks(TimeSpan.TicksPerSecond - 1);
        var openingHours = new[] { MakeOpenHour(2, new TimeOnly(8, 0), new TimeOnly(17, 0)) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeTrue();
    }

    // --- IsOpenNow: exact closing time (exclusive) ---

    [Fact]
    public void IsOpenNow_AtExactClosingTime_ReturnsFalse()
    {
        // 2026-09-08 is Tuesday day=2, closeTime=17:00 Vietnam → 10:00 UTC
        var utcNow = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        var openingHours = new[] { MakeOpenHour(2, new TimeOnly(8, 0), new TimeOnly(17, 0)) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeFalse();
    }

    // --- IsOpenNow: Sunday day=0 ---

    [Fact]
    public void IsOpenNow_SundayDayZeroWithMatchingHours_ReturnsTrue()
    {
        // Sunday 2026-09-13 at 10:00 Vietnam → 03:00 UTC
        var utcNow = new DateTimeOffset(2026, 9, 13, 3, 0, 0, TimeSpan.Zero);
        var openingHours = new[] { MakeOpenHour(0, new TimeOnly(5, 0), new TimeOnly(21, 0)) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeTrue();
    }

    // --- IsOpenNow: missing day entry → closed ---

    [Fact]
    public void IsOpenNow_MissingDayEntry_ReturnsFalse()
    {
        // Wednesday day=3, but opening hours only has Monday day=1
        var utcNow = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);
        var openingHours = new[] { MakeOpenHour(1, new TimeOnly(8, 0), new TimeOnly(17, 0)) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeFalse();
    }

    // --- IsOpenNow: day marked isClosed=true ---

    [Fact]
    public void IsOpenNow_DayMarkedClosed_ReturnsFalse()
    {
        // Wednesday day=3 exists but isClosed=true
        var utcNow = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);
        var openingHours = new[] { MakeClosedHour(3) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeFalse();
    }

    // --- IsOpenNow: empty opening hours → closed ---

    [Fact]
    public void IsOpenNow_EmptyOpeningHours_ReturnsFalse()
    {
        var utcNow = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);

        var result = PoiOpeningState.IsOpenNow(Array.Empty<PoiOpeningHour>(), utcNow);

        result.Should().BeFalse();
    }

    // --- UTC+07:00 conversion is independent of host timezone ---

    [Fact]
    public void IsOpenNow_ConvertsUtcToVietnamTimeCorrectly()
    {
        // 2026-09-09 23:30 UTC → 2026-09-10 06:30 Vietnam time → day=4 (Thursday)
        // If we have Thursday open 06:00–22:00, should be open
        var utcNow = new DateTimeOffset(2026, 9, 9, 23, 30, 0, TimeSpan.Zero);
        var openingHours = new[] { MakeOpenHour(4, new TimeOnly(6, 0), new TimeOnly(22, 0)) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsOpenNow_DateChangeDueToTimezoneOffset_UsesVietnamDay()
    {
        // 2026-09-09 23:30 UTC → 2026-09-10 06:30 Vietnam
        // Wednesday day=3 should NOT match; Thursday day=4 SHOULD
        var utcNow = new DateTimeOffset(2026, 9, 9, 23, 30, 0, TimeSpan.Zero);
        var openingHours = new[] { MakeOpenHour(3, new TimeOnly(6, 0), new TimeOnly(22, 0)) };

        var result = PoiOpeningState.IsOpenNow(openingHours, utcNow);

        result.Should().BeFalse();
    }

    // --- GetVietnamDayAndTime returns correct values ---

    [Fact]
    public void GetVietnamDayAndTime_ReturnsCorrectDayAndTime()
    {
        // 2026-09-09 03:00 UTC → 2026-09-09 10:00 Vietnam → Wednesday day=3
        var utcNow = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);

        var (day, time) = PoiOpeningState.GetVietnamDayAndTime(utcNow);

        day.Should().Be(3);
        time.Should().Be(new TimeOnly(10, 0));
    }

    [Fact]
    public void GetVietnamDayAndTime_SundayMapsToZero()
    {
        // 2026-09-13 is Sunday. 00:00 Vietnam → 2026-09-12 17:00 UTC
        var utcNow = new DateTimeOffset(2026, 9, 12, 17, 0, 0, TimeSpan.Zero);

        var (day, _) = PoiOpeningState.GetVietnamDayAndTime(utcNow);

        day.Should().Be(0);
    }

    [Fact]
    public void GetVietnamDayAndTime_SaturdayMapsToDaySix()
    {
        // 2026-09-12 is Saturday. 10:00 Vietnam → 03:00 UTC
        var utcNow = new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero);

        var (day, _) = PoiOpeningState.GetVietnamDayAndTime(utcNow);

        day.Should().Be(6);
    }

    // --- Helper for constructing PoiOpeningHour entities ---

    private static PoiOpeningHour MakeOpenHour(byte dayOfWeek, TimeOnly openTime, TimeOnly closeTime)
        => PoiOpeningHour.Create(dayOfWeek, openTime, closeTime, isClosed: false);

    private static PoiOpeningHour MakeClosedHour(byte dayOfWeek)
        => PoiOpeningHour.Create(dayOfWeek, null, null, isClosed: true);
}