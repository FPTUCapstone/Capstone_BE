using FluentAssertions;

using TripMate.Application.Features.Scheduling.TimeOfDay;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.TimeOfDay;

public sealed class TimeOfDayScorerTests
{
    private static readonly TimeOfDayTraits Beach = new(IndoorOutdoorType.Outdoor, false, "Beach", 9m, ["Ngắm bình minh", "Chèo SUP"]);
    private static readonly TimeOfDayTraits Mountain = new(IndoorOutdoorType.Outdoor, false, "Natural attraction", 8m, []);
    private static readonly TimeOfDayTraits Museum = new(IndoorOutdoorType.Indoor, true, "Museum", 5m, []);
    private static readonly TimeOfDayTraits Market = new(IndoorOutdoorType.Mixed, false, "Culture", 4m, []);

    [Fact]
    public void Sunrise_FavoursBeachesAndSunriseSpots()
    {
        TimeOfDayScorer.Score(new TimeOnly(5, 0), Beach).Should().BeGreaterThan(TimeOfDayScorer.Score(new TimeOnly(5, 0), Museum));
        TimeOfDayScorer.Reason(new TimeOnly(5, 0), Beach).Should().Be("Xếp lúc bình minh: trời mát, ngắm mặt trời mọc, chèo SUP");
    }

    [Fact]
    public void Morning_FavoursOutdoorAndNature()
    {
        TimeOfDayScorer.Score(new TimeOnly(8, 0), Mountain).Should().BePositive();
        TimeOfDayScorer.Score(new TimeOnly(8, 0), Museum).Should().Be(0);
        TimeOfDayScorer.Reason(new TimeOnly(8, 0), Mountain).Should().Be("Xếp vào buổi sáng khi trời mát, ít đông");
    }

    [Fact]
    public void Noon_PenalisesOutdoorWithoutShelter_AndHalvesItForMixed()
    {
        var outdoor = TimeOfDayScorer.Score(new TimeOnly(12, 0), Mountain);
        var mixed = TimeOfDayScorer.Score(new TimeOnly(12, 0), Market);

        outdoor.Should().BeNegative();
        mixed.Should().BeNegative().And.BeGreaterThan(outdoor);
        TimeOfDayScorer.Score(new TimeOnly(12, 0), Museum).Should().Be(0);
        TimeOfDayScorer.Reason(new TimeOnly(12, 0), Museum).Should().Be("Xếp vào buổi trưa để tránh nắng");
        TimeOfDayScorer.Reason(new TimeOnly(12, 0), Mountain).Should().Be("Điểm ngoài trời vào giữa trưa, nên mang nón và nước");
    }

    [Fact]
    public void Sunset_FavoursBeachesAndScenicSpots()
    {
        TimeOfDayScorer.Score(new TimeOnly(17, 0), Beach).Should().BePositive();
        TimeOfDayScorer.Reason(new TimeOnly(17, 0), Mountain).Should().Be("Xếp vào cuối chiều để ngắm hoàng hôn");
        TimeOfDayScorer.Score(new TimeOnly(17, 0), Museum).Should().Be(0);
    }

    [Fact]
    public void Afternoon_IsNeutral()
    {
        TimeOfDayScorer.Score(new TimeOnly(15, 0), Mountain).Should().Be(0);
        TimeOfDayScorer.Reason(new TimeOnly(15, 0), Mountain).Should().BeNull();
    }
}