using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>Turns a scheduled item into the persisted itinerary item, including the leg to the next stop.</summary>
public static class GeneratedItineraryItemMapper
{
    public static ItineraryItem ToEntity(GeneratedItineraryItem item, bool isMandatory) =>
        item.Kind == ItineraryItemKind.Rest
            ? ItineraryItem.CreateRest(
                item.SequenceNo,
                item.PointOfInterestId,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                // Saved in the wording travelers read; the plan keeps the solver's code.
                RestStopReasons.ForTraveler(item.Kind, item.RecommendationReason)!,
                item.TravelDurationToNextMinutes,
                travelDistanceToNextMeters: item.TravelDistanceToNextMeters,
                transportModeToNext: item.TransportModeToNext)
            : ItineraryItem.CreateVisit(
                item.SequenceNo,
                item.PointOfInterestId!.Value,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                isMandatory,
                item.EstimatedCost,
                item.RecommendationReason,
                item.TravelDurationToNextMinutes,
                travelDistanceToNextMeters: item.TravelDistanceToNextMeters,
                transportModeToNext: item.TransportModeToNext);
}