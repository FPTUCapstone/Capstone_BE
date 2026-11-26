using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public sealed class ItineraryItemCopyTests
{
    private static readonly DateTimeOffset Arrival = new(2026, 11, 24, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CopyForVersion_CopiesEveryPlanningFieldIntoAnUnattachedRow()
    {
        var source = ItineraryItem.CreateVisit(
            3, 55, Arrival, Arrival.AddMinutes(90), isMandatory: true, estimatedCost: 120_000m,
            recommendationReason: "Matches your interests", travelDurationToNextMinutes: 12,
            travelDistanceToNextMeters: 4200, transportModeToNext: TransportMode.Car);
        source.AttachFriendlyExplanation("A quiet start to the day.");

        var copy = source.CopyForVersion();

        copy.Should().NotBeSameAs(source);
        copy.Id.Should().Be(0);
        copy.ItineraryId.Should().Be(0);
        copy.Itinerary.Should().BeNull();
        copy.SequenceNo.Should().Be(3);
        copy.Kind.Should().Be(ItineraryItemKind.Visit);
        copy.PointOfInterestId.Should().Be(55);
        copy.PlannedArrivalUtc.Should().Be(Arrival);
        copy.PlannedDepartureUtc.Should().Be(Arrival.AddMinutes(90));
        copy.StayDurationMinutes.Should().Be(90);
        copy.IsMandatory.Should().BeTrue();
        copy.EstimatedCost.Should().Be(120_000m);
        copy.RecommendationReason.Should().Be("Matches your interests");
        copy.FriendlyExplanation.Should().Be("A quiet start to the day.");
        copy.TravelDurationToNextMinutes.Should().Be(12);
        copy.TravelDistanceToNextMeters.Should().Be(4200);
        copy.TransportModeToNext.Should().Be(TransportMode.Car);
        copy.Status.Should().Be(source.Status);
    }

    [Fact]
    public void CopyForVersion_CopiesARestStopWithoutAPoi()
    {
        var source = ItineraryItem.CreateRest(2, Arrival, Arrival.AddMinutes(30), "rest_stop");

        var copy = source.CopyForVersion();

        copy.Kind.Should().Be(ItineraryItemKind.Rest);
        copy.PointOfInterestId.Should().BeNull();
        copy.IsMandatory.Should().BeFalse();
        copy.SequenceNo.Should().Be(2);
    }
}