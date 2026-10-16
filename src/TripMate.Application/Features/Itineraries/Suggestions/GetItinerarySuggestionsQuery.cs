using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.PointsOfInterest.ExternalReviews;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Suggestions;

/// <summary>
/// Places the traveler can add to or swap into the current itinerary. <c>Food</c> suggests restaurants to
/// insert after <see cref="AfterItemId"/>; <c>Replace</c> suggests places of the same category as the stop
/// <see cref="ReplaceItemId"/>. Each suggestion is scheduled first (nothing saved) to tell whether it fits.
/// </summary>
public sealed record GetItinerarySuggestionsQuery(
    long ItineraryId,
    long TravelerUserId,
    string Kind,
    long? AfterItemId,
    long? ReplaceItemId)
    : IRequest<Result<ItinerarySuggestionsDto>>
{
    public const string KindFood = "Food";
    public const string KindReplace = "Replace";
    public static readonly string[] AllowedKinds = [KindFood, KindReplace];

    /// <summary>Nearest places scheduled per request; the best are returned.</summary>
    public const int CandidatesToEvaluate = 10;

    public const int MaxSuggestions = 5;
}

public sealed record ItinerarySuggestionsDto(
    long ItineraryId,
    string Kind,
    decimal SearchRadiusKm,
    IReadOnlyList<ItinerarySuggestionDto> Items);

/// <summary>
/// <see cref="FitsItinerary"/> is null when routing was unavailable and only straight-line distance is known.
/// <see cref="DetourMinutes"/> is how much longer the whole trip becomes (negative when it gets shorter).
/// <see cref="UnfitPoiId"/> is the stop that breaks: this place, or a later stop it pushes past closing time.
/// A null <see cref="UnfitReason"/> with <c>FitsItinerary = false</c> means the cause could not be pinned down.
/// </summary>
public sealed record ItinerarySuggestionDto(
    long PoiId,
    string Name,
    string? Category,
    string? ThumbnailUrl,
    string? Address,
    CostKind CostKind,
    decimal? EstimatedCost,
    int? DistanceFromPreviousMeters,
    int? DetourMinutes,
    string DistanceSource,
    DateTimeOffset? EstimatedArrival,
    bool? OpenAtArrival,
    string? ClosesAt,
    bool? FitsItinerary,
    string? UnfitReason,
    long? UnfitPoiId,
    int? OverflowMinutes,
    decimal? OverflowVnd,
    TripadvisorRating? Tripadvisor);

public static class SuggestionDistanceSources
{
    public const string Road = "Road";
    public const string StraightLine = "StraightLine";
}