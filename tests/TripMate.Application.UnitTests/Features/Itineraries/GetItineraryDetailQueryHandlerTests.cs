using FluentAssertions;

using TripMate.Application.Features.Itineraries.GetDetail;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class GetItineraryDetailQueryHandlerTests
{
    private static readonly DateTimeOffset StartAtUtc = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToResponse_UsesPersistedRoadDurationInsteadOfTheArrivalGap()
    {
        var itinerary = Itinerary.CreateManual(1, "Travel duration", Itinerary.ActiveStatus, StartAtUtc);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1,
            101,
            StartAtUtc,
            StartAtUtc.AddMinutes(30),
            false,
            0m,
            null,
            travelDurationToNextMinutes: 15));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            2,
            102,
            StartAtUtc.AddMinutes(65),
            StartAtUtc.AddMinutes(95),
            false,
            0m,
            null));

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, 1);

        response.Items.ElementAt(0).TravelDurationFromPreviousMinutes.Should().BeNull();
        response.Items.ElementAt(1).TravelDurationFromPreviousMinutes.Should().Be(15);
    }

    [Fact]
    public void ToResponse_WhenPreviousItemHasNoPersistedRoadDuration_ReturnsNull()
    {
        var itinerary = Itinerary.CreateManual(1, "Legacy itinerary", Itinerary.ActiveStatus, StartAtUtc);
        itinerary.AddItem(ItineraryItem.CreateVisit(
            1,
            101,
            StartAtUtc,
            StartAtUtc.AddMinutes(30),
            false,
            0m,
            null));
        itinerary.AddItem(ItineraryItem.CreateVisit(
            2,
            102,
            StartAtUtc.AddMinutes(65),
            StartAtUtc.AddMinutes(95),
            false,
            0m,
            null));

        var response = GetItineraryDetailQueryHandler.ToResponse(itinerary, true, 1);

        response.Items.ElementAt(1).TravelDurationFromPreviousMinutes.Should().BeNull();
    }
}