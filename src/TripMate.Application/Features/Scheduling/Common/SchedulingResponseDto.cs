using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed record SchedulingResponseDto(
    long SchedulingRequestId,
    long ItineraryId,
    string Title,
    string Status,
    decimal TotalEstimatedCost,
    int TotalDurationMinutes,
    IReadOnlyCollection<SchedulingItemDto> Items,
    SchedulingProvenanceDto? Provenance = null)
{
    /// <summary>
    /// True when the items carry template sentences and the AI explanation is being written in the background
    /// (specs/UC-10-background-explanation-spec.md, B1). A client may then load the itinerary detail again later;
    /// the sentences there change once the AI has answered. False in every other case, also on a replay.
    /// </summary>
    public bool ExplanationPending { get; init; }
}

public sealed record SchedulingItemDto(
    int SequenceNo,
    long? PoiId,
    string? PoiName,
    ItineraryItemKind ItemKind,
    DateTimeOffset PlannedArrival,
    DateTimeOffset PlannedDeparture,
    int StayDurationMinutes,
    int? TravelDurationToNextMinutes,
    decimal? EstimatedCost,
    bool IsMandatory,
    string? RecommendationReason,
    string? FriendlyExplanation,
    CostKind CostKind = CostKind.AdmissionTicket,
    int? TravelDistanceToNextMeters = null,
    TransportMode? TransportModeToNext = null);