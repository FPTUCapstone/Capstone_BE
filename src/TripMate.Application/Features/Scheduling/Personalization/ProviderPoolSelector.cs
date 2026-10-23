namespace TripMate.Application.Features.Scheduling.Personalization;

internal sealed record ProviderPoolSelectionCandidate(
    PoiRankingInputCandidate Candidate,
    decimal BaseScore);

internal sealed record ProviderPoolSelection(
    IReadOnlyList<ProviderPoolSelectionCandidate> Candidates,
    int DroppedCount);

internal static class ProviderPoolSelector
{
    public static ProviderPoolSelection Select(
        IReadOnlyCollection<ProviderPoolSelectionCandidate> candidates,
        int maximumCandidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCandidates);

        long? duplicateId = candidates
            .GroupBy(candidate => candidate.Candidate.PoiId)
            .Where(group => group.Count() > 1)
            .Select(group => (long?)group.Key)
            .Order()
            .FirstOrDefault();
        if (duplicateId.HasValue)
        {
            throw new ArgumentException(
                $"Provider-pool candidates contain duplicate POI ID {duplicateId.Value}.",
                nameof(candidates));
        }

        ProviderPoolSelectionCandidate[] selected = candidates
            .OrderByDescending(candidate => candidate.BaseScore)
            .ThenByDescending(candidate => candidate.Candidate.ScenicScore ?? decimal.MinValue)
            .ThenByDescending(candidate => candidate.Candidate.PhotoRating ?? decimal.MinValue)
            .ThenBy(candidate => candidate.Candidate.ExplorationDistanceForRanking)
            .ThenBy(candidate =>
                candidate.Candidate.EstimatedVisitCostForRanking ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.Candidate.PoiId)
            .Take(maximumCandidates)
            .ToArray();

        return new ProviderPoolSelection(selected, candidates.Count - selected.Length);
    }
}