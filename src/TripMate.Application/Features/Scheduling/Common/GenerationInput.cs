using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public sealed record GenerationOpeningHours(byte DayOfWeek, TimeOnly OpenTime, TimeOnly CloseTime);

public sealed record GenerationCandidate(
    long Id,
    string Name,
    RoutePoint Location,
    int VisitDurationMinutes,
    decimal? EstimatedVisitCost,
    IReadOnlyCollection<GenerationOpeningHours> OpeningHours,
    int PreferenceScore = 0,
    decimal? ScenicScore = null,
    decimal? PhotoRating = null,
    string? CategoryName = null,
    bool HasShelter = false);

public sealed record GenerationInput(
    DateTimeOffset StartAtUtc,
    TimeZoneInfo TimeZone,
    RoutePoint Start,
    RoutePoint End,
    int AvailableMinutes,
    TransportMode TransportMode,
    RestPreference RestPreference,
    decimal? BudgetVnd,
    IReadOnlyCollection<GenerationCandidate> Candidates,
    IReadOnlyCollection<long> MandatoryPoiIds);

public sealed record GeneratedItineraryItem(
    int SequenceNo,
    long? PointOfInterestId,
    string? PointOfInterestName,
    ItineraryItemKind Kind,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    bool IsMandatory,
    decimal? EstimatedCost,
    string RecommendationReason,
    int? TravelDurationToNextMinutes = null);

public sealed record GeneratedItineraryPlan(
    IReadOnlyCollection<GeneratedItineraryItem> Items,
    DateTimeOffset EndAtUtc,
    int TotalDurationMinutes,
    decimal TotalEstimatedCost);