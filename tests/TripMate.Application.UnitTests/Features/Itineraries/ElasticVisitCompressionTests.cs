using FluentAssertions;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class ElasticVisitCompressionTests
{
    private static readonly DateTimeOffset StartAtUtc = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PoiCategoryNames.FoodAndDrink, 35)]
    [InlineData("Museum", 30)]
    [InlineData(null, 30)]
    public void GetMinFloorMinutes_ReturnsCorrectFloorPerCategory(string? category, int expectedFloor)
    {
        ElasticVisitCompression.GetMinFloorMinutes(category).Should().Be(expectedFloor);
    }

    [Theory]
    [InlineData(60, "Museum", 45)] // 60 * 0.75 = 45 >= 30
    [InlineData(30, "Museum", 30)] // 30 * 0.75 = 23 < 30 -> floor 30
    [InlineData(40, PoiCategoryNames.FoodAndDrink, 35)] // 40 * 0.75 = 30 < 35 -> floor 35
    [InlineData(60, PoiCategoryNames.FoodAndDrink, 45)] // 60 * 0.75 = 45 >= 35
    public void CalculateMinDuration_CompressesUpTo25PercentRespectingFloor(int defaultMinutes, string? category, int expectedMin)
    {
        ElasticVisitCompression.CalculateMinDuration(defaultMinutes, category).Should().Be(expectedMin);
    }

    [Fact]
    public void ApplyCompression_ProportionalDistribution_AbsorbsTargetOverflow()
    {
        var c1 = Candidate(1, 60, "Museum"); // max reduction: 15m
        var c2 = Candidate(2, 60, "Beach"); // max reduction: 15m
        var input = CreateInput(c1, c2);

        // Need 10 minutes absorbed: should reduce each by 5 minutes
        var compressed = ElasticVisitCompression.ApplyCompression(input, [1, 2], neededMinutes: 10);
        var map = compressed.Candidates.ToDictionary(c => c.Id);

        map[1].VisitDurationMinutes.Should().Be(55);
        map[2].VisitDurationMinutes.Should().Be(55);
    }

    [Fact]
    public void ApplyCompression_WithoutNeededMinutes_AppliesMaxAllowableCompression()
    {
        var c1 = Candidate(1, 60, "Museum"); // 60 -> 45
        var c2 = Candidate(2, 30, "Attraction"); // 30 -> 30 (floor)
        var c3 = Candidate(3, 40, PoiCategoryNames.FoodAndDrink); // 40 -> 35 (floor)
        var input = CreateInput(c1, c2, c3);

        var compressed = ElasticVisitCompression.ApplyCompression(input, [1, 2, 3]);
        var map = compressed.Candidates.ToDictionary(c => c.Id);

        map[1].VisitDurationMinutes.Should().Be(45);
        map[2].VisitDurationMinutes.Should().Be(30);
        map[3].VisitDurationMinutes.Should().Be(35);
    }

    private static GenerationInput CreateInput(params GenerationCandidate[] candidates) =>
        new(
            StartAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            new RoutePoint(16.0544m, 108.2022m),
            new RoutePoint(16.0544m, 108.2022m),
            480,
            TransportMode.Motorbike,
            RestPreference.None,
            null,
            candidates,
            []);

    private static GenerationCandidate Candidate(long id, int duration, string? category) =>
        new(
            id,
            $"Place {id}",
            new RoutePoint(16.0544m, 108.2022m),
            duration,
            0m,
            [],
            0m,
            0m,
            null,
            null,
            null,
            CategoryName: category);
}
