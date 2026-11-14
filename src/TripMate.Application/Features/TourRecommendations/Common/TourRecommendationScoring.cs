using System.Globalization;
using System.Text;

namespace TripMate.Application.Features.TourRecommendations.Common;

internal static class TourRecommendationScoring
{
    private const decimal InterestWeight = 40m;
    private const decimal DestinationWeight = 20m;
    private const decimal DateWeight = 15m;
    private const decimal BudgetWeight = 15m;
    private const decimal HistoryWeight = 10m;
    private const decimal MinimumEffectiveThreshold = 80m;

    internal static TourRecommendationScore Calculate(TourRecommendationScoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var travelerInterests = NormalizeTokens(input.TravelerInterestTokens);
        var candidateInterests = NormalizeTokens(input.CandidateInterestTokens);
        var history = NormalizeTokens(input.CompletedHistoryTokens);
        var candidateHistory = NormalizeTokens(input.CandidateHistoryTokens);
        var destinations = NormalizeTokens(input.CandidateDestinations);
        string? requestedDestination = NormalizeOptional(input.RequestedDestination);

        decimal weightedScore = InterestWeight * JaccardNormalized(travelerInterests, candidateInterests);
        decimal activeWeight = InterestWeight;
        var reasons = new List<string>(5);

        if (JaccardNormalized(travelerInterests, candidateInterests) > 0)
        {
            reasons.Add("Interest tags");
        }

        if (requestedDestination is not null)
        {
            bool matches = destinations.Contains(requestedDestination, StringComparer.Ordinal);
            activeWeight += DestinationWeight;
            if (matches)
            {
                weightedScore += DestinationWeight;
                reasons.Add("Destination");
            }
        }

        if (input.HasDateCriteria)
        {
            activeWeight += DateWeight;
            if (input.DateMatches)
            {
                weightedScore += DateWeight;
                reasons.Add("Date");
            }
        }

        if (input.MinBudget.HasValue || input.MaxBudget.HasValue)
        {
            activeWeight += BudgetWeight;
            bool withinMinimum = !input.MinBudget.HasValue || input.CandidatePrice >= input.MinBudget.Value;
            bool withinMaximum = !input.MaxBudget.HasValue || input.CandidatePrice <= input.MaxBudget.Value;
            if (withinMinimum && withinMaximum)
            {
                weightedScore += BudgetWeight;
                reasons.Add("Budget");
            }
        }

        if (history.Count > 0)
        {
            activeWeight += HistoryWeight;
            decimal similarity = JaccardNormalized(history, candidateHistory);
            weightedScore += HistoryWeight * similarity;
            if (similarity > 0)
            {
                reasons.Add("Trip history");
            }
        }

        decimal score = decimal.Round(
            weightedScore / activeWeight * 100m,
            2,
            MidpointRounding.AwayFromZero);
        return new TourRecommendationScore(score, reasons);
    }

    internal static bool Qualifies(decimal score, decimal configuredThreshold) =>
        score > Math.Max(configuredThreshold, MinimumEffectiveThreshold);

    internal static decimal Jaccard(
        IEnumerable<string> left,
        IEnumerable<string> right) =>
        JaccardNormalized(NormalizeTokens(left), NormalizeTokens(right));

    internal static IReadOnlyList<string> NormalizeTokens(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeToken)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static decimal JaccardNormalized(
        IReadOnlyCollection<string> left,
        IReadOnlyCollection<string> right)
    {
        if (left.Count == 0 && right.Count == 0)
        {
            return 0m;
        }

        int intersection = left.Intersect(right, StringComparer.Ordinal).Count();
        int union = left.Union(right, StringComparer.Ordinal).Count();
        return (decimal)intersection / union;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeToken(value);

    private static string NormalizeToken(string value) =>
        new string(value
            .Trim()
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character)
                != UnicodeCategory.NonSpacingMark)
            .ToArray())
        .ToLowerInvariant()
        .Replace('đ', 'd');
}