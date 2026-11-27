namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

public sealed record PagedFavoritePoisResponse(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyCollection<FavoritePoiItemDto> Items);