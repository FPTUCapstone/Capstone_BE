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
    IReadOnlyCollection<string> CandidateHistoryTokens,
    string? TravelerPace = null,
    string? CandidatePace = null,
    IReadOnlyCollection<string>? TravelerTransportTokens = null,
    IReadOnlyCollection<string>? CandidateTransportTokens = null,
    IReadOnlyCollection<string>? TravelerDietTokens = null,
    IReadOnlyCollection<string>? CandidateDietTokens = null);

internal sealed record TourRecommendationScore(
    decimal Score,
    IReadOnlyList<string> MatchingReasons);