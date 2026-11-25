using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.UnitTests.Features.Scheduling;

/// <summary>
/// ADR-002 G5: in WeightedUtility mode each visit says, in Vietnamese, which parts of J chose it. The text is shown as-is by
/// Web and Mobile and feeds the itinerary explanation ("sở thích" and "gần" select its preference and nearby templates).
/// </summary>
public sealed class PersonalizedVisitReasonsTests
{
    [Fact]
    public void Describe_NamesTheTravelersOwnChoiceFirst()
    {
        PersonalizedVisitReasons.Describe(isMandatory: true, matchesInterests: true, "Culture", timeFitFactor: 1.2, travelMinutes: 5)
            .Should().Be("Điểm bạn đã chọn · Đi vào khung giờ đẹp");
    }

    [Fact]
    public void Describe_ListsInterestMatchWithItsCategory_GoodTimeAndShortDetour()
    {
        PersonalizedVisitReasons.Describe(isMandatory: false, matchesInterests: true, "Bảo tàng", timeFitFactor: 1.1, travelMinutes: 12)
            .Should().Be("Hợp sở thích của bạn (Bảo tàng) · Đi vào khung giờ đẹp · Gần lộ trình");
    }

    [Theory]
    [InlineData(1.04, 16)]
    [InlineData(0.7, 40)]
    public void Describe_FallsBackToTheTripFit_WhenNoComponentStandsOut(double timeFitFactor, int travelMinutes)
    {
        PersonalizedVisitReasons.Describe(isMandatory: false, matchesInterests: false, "Culture", timeFitFactor, travelMinutes)
            .Should().Be("Vừa thời gian chuyến đi");
    }

    /// <summary>
    /// The time-of-day step names the slot more precisely ("buổi sáng khi trời mát"), so it replaces the generic time
    /// part and the fallback, and keeps the interest, own-choice and detour parts in front of it.
    /// </summary>
    [Theory]
    [InlineData("Hợp sở thích của bạn (Nature) · Đi vào khung giờ đẹp · Gần lộ trình", "Hợp sở thích của bạn (Nature) · Gần lộ trình · Buổi sáng mát")]
    [InlineData("Điểm bạn đã chọn", "Điểm bạn đã chọn · Buổi sáng mát")]
    [InlineData("Vừa thời gian chuyến đi", "Buổi sáng mát")]
    [InlineData("Suggested nearby location", "Buổi sáng mát")]
    public void WithTimeOfDay_KeepsThePersonalPartsAndAppendsTheSlotReason(string reason, string expected)
    {
        PersonalizedVisitReasons.WithTimeOfDay(reason, "Buổi sáng mát").Should().Be(expected);
    }

    [Fact]
    public void Describe_OmitsTheCategory_WhenItIsUnknown()
    {
        PersonalizedVisitReasons.Describe(isMandatory: false, matchesInterests: true, "  ", timeFitFactor: 1.0, travelMinutes: 30)
            .Should().Be("Hợp sở thích của bạn");
    }
}