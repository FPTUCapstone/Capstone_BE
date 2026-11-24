using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.List;

public sealed record ItinerarySummaryResponse(
    long ItineraryId,
    long? SchedulingRequestId,
    string? Title,
    int Version,
    string Status,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    bool CanManage,
    int StopCount,
    long? OpenNavigationSessionId,
    DateTimeOffset? CreatedAt = null,
    TransportMode? TransportMode = null,
    IReadOnlyList<string>? PreviewStopNames = null);

public sealed record ItinerarySummaryPage(
    IReadOnlyCollection<ItinerarySummaryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);