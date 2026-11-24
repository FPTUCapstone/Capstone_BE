using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// UC-15: evaluates the remaining part of a trip instead of the whole day. When given to
/// <see cref="IItineraryVersionService.EvaluateStopVariantsAsync"/> it replaces the request's start time,
/// start point, time budget and money budget, and bounds the candidate POIs by <see cref="CandidateRadiusKm"/>
/// around <see cref="CandidateCenter"/> (the variant stops are always kept, so a replacement is never lost
/// to the trip radius).
/// </summary>
/// <param name="StartAtUtc">The earliest time the Traveler can leave: <c>max(now, planned departure of the stop being explored)</c>.</param>
/// <param name="Start">The last reached or exploring stop, else the request start point.</param>
/// <param name="AvailableMinutes">Minutes from <paramref name="StartAtUtc"/> to the original end of the day.</param>
/// <param name="RemainingBudgetVnd">The request budget minus what reached stops cost; null when the trip has no budget.</param>
/// <param name="CandidateRadiusKm"><c>Rerouting.SearchRadiusKm</c>.</param>
/// <param name="CandidateCenter">The affected stop.</param>
public sealed record EvaluationAnchor(
    DateTimeOffset StartAtUtc,
    RoutePoint Start,
    int AvailableMinutes,
    decimal? RemainingBudgetVnd,
    decimal CandidateRadiusKm,
    RoutePoint CandidateCenter);