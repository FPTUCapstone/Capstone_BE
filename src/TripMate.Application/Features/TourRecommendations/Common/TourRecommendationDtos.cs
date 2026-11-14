using System.Text.Json.Serialization;

using TripMate.Application.Features.Tours.Search;

namespace TripMate.Application.Features.TourRecommendations.Common;

public sealed record TourRecommendationCriteriaDto(
    string? Destination,
    DateOnly? StartDate,
    DateOnly? EndDate,
    decimal? MinBudget,
    decimal? MaxBudget);

public sealed record TourRecommendationItemDto(
    [property: JsonRequired] TourSearchItemDto Tour,
    [property: JsonRequired] decimal MatchingScore,
    [property: JsonRequired] IReadOnlyList<string> MatchingReasons);

public sealed record TourRecommendationPageDto(
    int Page,
    int PageSize,
    long TotalCount,
    long TotalPages,
    DateTime AsOfUtc,
    TourRecommendationCriteriaDto? Criteria,
    IReadOnlyList<TourRecommendationItemDto> Items);
