using FluentAssertions;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public class PoiOpeningHourTests
{
    [Fact]
    public void Create_WithOpenDay_StoresTimeRange()
    {
        var openingHour = PoiOpeningHour.Create(
            1,
            new TimeOnly(7, 0),
            new TimeOnly(17, 30),
            false);

        openingHour.DayOfWeek.Should().Be(1);
        openingHour.OpenTime.Should().Be(new TimeOnly(7, 0));
        openingHour.CloseTime.Should().Be(new TimeOnly(17, 30));
        openingHour.IsClosed.Should().BeFalse();
    }

    [Fact]
    public void Create_WithClosedDayAndNoTimes_Succeeds()
    {
        var openingHour = PoiOpeningHour.Create(0, null, null, true);

        openingHour.IsClosed.Should().BeTrue();
        openingHour.OpenTime.Should().BeNull();
        openingHour.CloseTime.Should().BeNull();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(255)]
    public void Create_WithInvalidDay_Throws(byte dayOfWeek)
    {
        var action = () => PoiOpeningHour.Create(dayOfWeek, null, null, true);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithClosedDayContainingTimes_Throws()
    {
        var action = () => PoiOpeningHour.Create(
            2,
            new TimeOnly(7, 0),
            new TimeOnly(17, 30),
            true);

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(7, 0, 7, 0)]
    [InlineData(18, 0, 7, 0)]
    public void Create_WithNonIncreasingOpenRange_Throws(
        int openHour,
        int openMinute,
        int closeHour,
        int closeMinute)
    {
        var action = () => PoiOpeningHour.Create(
            2,
            new TimeOnly(openHour, openMinute),
            new TimeOnly(closeHour, closeMinute),
            false);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithOpenDayMissingATime_Throws()
    {
        var action = () => PoiOpeningHour.Create(2, new TimeOnly(7, 0), null, false);

        action.Should().Throw<ArgumentException>();
    }
}
