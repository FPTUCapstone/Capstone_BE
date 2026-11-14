namespace TripMate.Application.Features.TourRecommendations.Common;

internal sealed record TourRecommendationScoreInput(
    IReadOnlyCollection<string> TravelerInterestTokens,
    IReadOnlyCollection<string> CandidateInterestTokens,
    string? RequestedDestination,
    IReadOnlyCollection<string> CandidateDestinations,
    bool HasDateCriteria,
    bool DateMatches,
    decimal? MinBudget,
    decimal? MaxBudget,
    decimal CandidatePrice,
    IReadOnlyCollection<string> CompletedHistoryTokens,
    IReadOnlyCollection<string> CandidateHistoryTokens);

internal sealed record TourRecommendationScore(
    decimal Score,
    IReadOnlyList<string> MatchingReasons);
