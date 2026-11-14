namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

/// <summary>The heart on a place detail page, as the asking traveler sees it.</summary>
public sealed record PoiFavoriteResponse(long PoiId, bool IsFavorite);