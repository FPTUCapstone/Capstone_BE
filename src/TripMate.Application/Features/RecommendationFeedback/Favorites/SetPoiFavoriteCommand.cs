using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

/// <summary>
/// Turns the heart on or off (UC-10 traveler feedback, D12). Both directions are idempotent, so a double tap or
/// a retry never fails. The ranking reads the favorites through <c>BehaviorProfileBuilder</c> (D17).
/// </summary>
public sealed record SetPoiFavoriteCommand(long TravelerUserId, long PoiId, bool IsFavorite)
    : IRequest<Result<PoiFavoriteResponse>>;