using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class ItineraryItemTests
{
    private static readonly DateTimeOffset Arrival = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Departure = new(2026, 10, 20, 3, 30, 0, TimeSpan.Zero);

    [Fact]
    public void CreateRest_WithoutPoi_CreatesTypedBreak()
    {
        var item = ItineraryItem.CreateRest(2, Arrival, Departure, "Free/rest time");

        item.SequenceNo.Should().Be(2);
        item.Kind.Should().Be(ItineraryItemKind.Rest);
        item.PointOfInterestId.Should().BeNull();
        item.IsMandatory.Should().BeFalse();
        item.StayDurationMinutes.Should().Be(30);
        item.RecommendationReason.Should().Be("Free/rest time");
    }

    [Fact]
    public void CreateVisit_WithoutPersistedPoi_Throws()
    {
        var action = () => ItineraryItem.CreateVisit(
            1,
            0,
            Arrival,
            Departure,
            isMandatory: false,
            estimatedCost: 40_000m,
            recommendationReason: "Popular cultural stop");

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateRest_WithNonPositiveDuration_Throws()
    {
        var action = () => ItineraryItem.CreateRest(
            1,
            Departure,
            Arrival,
            "Free/rest time");

        action.Should().Throw<ArgumentException>();
    }
}