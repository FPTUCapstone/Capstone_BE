namespace TripMate.Application.Features.Personalization.Recommendations;

public sealed record PoiRecommendationResultDto(
    IReadOnlyCollection<RecommendedPoiItemDto> Items,
    int TotalAvailable);
