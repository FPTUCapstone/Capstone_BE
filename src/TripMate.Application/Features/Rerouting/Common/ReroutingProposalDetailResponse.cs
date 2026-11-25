namespace TripMate.Application.Features.Rerouting.Common;

/// <summary>
/// The Re-routing Suggestion screen (SRS 3.3.6): the current and proposed plans side by side, the reason and the
/// differences. <c>Status</c> is the effective status: a Proposed event past its TTL reads as Expired.
/// </summary>
public sealed record ReroutingProposalDetailResponse(
    long ProposalId,
    long IncidentId,
    string Status,
    ReroutingReasonResponse Reason,
    IReadOnlyList<ReroutingCurrentStopResponse> CurrentStops,
    IReadOnlyList<ReroutingProposedStopResponse> ProposedStops,
    IReadOnlyList<ReroutingAffectedStopResponse> AffectedStops,
    IReadOnlyList<ReroutingRemovedStopResponse> RemovedStops,
    int TravelTimeDiffMinutes,
    int ArrivalTimeDiffMinutes,
    DateTimeOffset ProposedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? DecidedAtUtc);

public sealed record ReroutingReasonResponse(
    string Code,
    string Strategy,
    long? AffectedItemId,
    string? AffectedPoiName);

public sealed record ReroutingCurrentStopResponse(
    long ItineraryItemId,
    long PoiId,
    string Name,
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    bool IsMandatory);

public sealed record ReroutingProposedStopResponse(
    long PoiId,
    string Name,
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    bool IsMandatory,
    string? TransportModeToNext,
    string Change);

public sealed record ReroutingAffectedStopResponse(long ItineraryItemId, long PoiId, string Name);

public sealed record ReroutingRemovedStopResponse(long PoiId, string Name);