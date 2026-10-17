using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1.Requests;

/// <summary>
/// Send <see cref="Stops"/> (preferred: per-leg transport) or the legacy <see cref="OrderedVisitPoiIds"/>.
/// </summary>
public sealed record AdjustItineraryItemsRequest(
    IReadOnlyCollection<long>? OrderedVisitPoiIds = null,
    IReadOnlyList<AdjustItineraryStopRequest>? Stops = null);

public sealed record AdjustItineraryStopRequest(long PoiId, TransportMode? TransportModeToNext = null);