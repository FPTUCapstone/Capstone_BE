using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.AdjustItems;

/// <summary>
/// Replaces the visits of an itinerary: add, replace, remove or reorder stops and choose the transport of
/// each leg. <see cref="Stops"/> carries the per-leg transport; legacy callers send only
/// <see cref="OrderedVisitPoiIds"/> and every leg keeps the trip transport.
/// </summary>
public sealed record AdjustItineraryItemsCommand(
    long ItineraryId,
    long TravelerUserId,
    Guid IdempotencyKey,
    IReadOnlyCollection<long> OrderedVisitPoiIds,
    IReadOnlyList<ItineraryStop>? Stops = null)
    : IRequest<Result<ItineraryDetailResponse>>
{
    public const int MaxStops = 20;

    /// <summary>Transport a traveler can choose for one leg (public transit has no routing yet).</summary>
    public static readonly TransportMode[] AllowedLegModes =
        [TransportMode.Walking, TransportMode.Motorbike, TransportMode.Car];

    public IReadOnlyList<ItineraryStop> EffectiveStops =>
        Stops ?? OrderedVisitPoiIds.Select(id => new ItineraryStop(id)).ToArray();
}