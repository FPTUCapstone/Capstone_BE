namespace TripMate.Application.Features.Personalization.Recommendations;

public sealed record RecommendedPoiItemDto(
    long PoiId,
    string Name,
    string CategoryName,
    string? ThumbnailUrl,
    decimal DistanceKm,
    decimal? EstimatedVisitCost,
    decimal? AverageRating,
    string? RecommendationReason);
