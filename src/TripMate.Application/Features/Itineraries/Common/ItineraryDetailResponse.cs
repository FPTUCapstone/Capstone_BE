using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

public sealed record ItineraryDetailResponse(
    long ItineraryId,
    long SchedulingRequestId,
    string? Title,
    int Version,
    string Status,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    bool CanManage,
    decimal TotalEstimatedCost,
    int TotalDurationMinutes,
    IReadOnlyCollection<ItineraryDetailItemDto> Items);

public sealed record ItineraryDetailItemDto(
    long ItemId,
    int SequenceNo,
    long? PoiId,
    string? PoiName,
    string? Category,
    ItineraryItemKind Kind,
    DateTimeOffset PlannedArrival,
    DateTimeOffset PlannedDeparture,
    int? TravelDurationFromPreviousMinutes,
    int StayDurationMinutes,
    decimal? EstimatedCost,
    bool IsMandatory,
    string? RecommendationReason,
    string? FriendlyExplanation,
    bool IsUnavailable);

public sealed record ItineraryAccess(
    ItineraryDetailAccessKind AccessKind,
    long RequestedItineraryId,
    long SchedulingRequestId,
    Itinerary CurrentItinerary,
    bool CanManage);

public enum ItineraryDetailAccessKind
{
    Owner = 1,
    GroupMember = 2,
}