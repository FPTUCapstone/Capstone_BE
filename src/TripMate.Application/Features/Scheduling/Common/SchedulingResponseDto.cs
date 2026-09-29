using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed record SchedulingResponseDto(
    long SchedulingRequestId,
    long ItineraryId,
    string Title,
    string Status,
    decimal TotalEstimatedCost,
    int TotalDurationMinutes,
    IReadOnlyCollection<SchedulingItemDto> Items);

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
    string? RecommendationReason);