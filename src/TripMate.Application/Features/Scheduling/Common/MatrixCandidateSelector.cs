using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.Features.Scheduling.Common;

internal sealed record MatrixCandidateSelection(
    IReadOnlyList<long> OrderedPoiIds,
    int OptionalCapacity,
    int SelectedOptionalCount,
    int DroppedOptionalCount);

internal static class MatrixCandidateSelector
{
    public static MatrixCandidateSelection Select(
        IReadOnlyCollection<long> mandatoryPoiIds,
        IReadOnlyCollection<PoiRankingSnapshotEntry> optionalCandidates,
        int maximumCandidates)
    {
        ArgumentNullException.ThrowIfNull(mandatoryPoiIds);
        ArgumentNullException.ThrowIfNull(optionalCandidates);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCandidates);

        long[] mandatory = mandatoryPoiIds.Order().ToArray();
        long? duplicateMandatory = mandatory
            .GroupBy(id => id)
            .Where(group => group.Count() > 1)
            .Select(group => (long?)group.Key)
            .FirstOrDefault();
        if (duplicateMandatory.HasValue)
        {
            throw Duplicate(duplicateMandatory.Value, nameof(mandatoryPoiIds));
        }

        long? duplicateOptional = optionalCandidates
            .GroupBy(candidate => candidate.PoiId)
            .Where(group => group.Count() > 1)
            .Select(group => (long?)group.Key)
            .Order()
            .FirstOrDefault();
        if (duplicateOptional.HasValue)
        {
            throw Duplicate(duplicateOptional.Value, nameof(optionalCandidates));
        }

        HashSet<long> mandatorySet = mandatory.ToHashSet();
        long? overlap = optionalCandidates
            .Where(candidate => mandatorySet.Contains(candidate.PoiId))
            .Select(candidate => (long?)candidate.PoiId)
            .Order()
            .FirstOrDefault();
        if (overlap.HasValue)
        {
            throw Duplicate(overlap.Value, nameof(optionalCandidates));
        }

        int optionalCapacity = Math.Max(0, maximumCandidates - mandatory.Length);
        long[] selectedOptionalIds = optionalCandidates
            .OrderByDescending(candidate => candidate.EffectiveDesirabilityScore)
            .ThenByDescending(candidate =>
                candidate.ScenicScoreForRanking ?? decimal.MinValue)
            .ThenByDescending(candidate =>
                candidate.PhotoRatingForRanking ?? decimal.MinValue)
            .ThenBy(candidate => candidate.ExplorationDistanceForRanking)
            .ThenBy(candidate =>
                candidate.EstimatedVisitCostForRanking ?? decimal.MaxValue)
            .ThenBy(candidate => candidate.PoiId)
            .Take(optionalCapacity)
            .Select(candidate => candidate.PoiId)
            .ToArray();

        return new MatrixCandidateSelection(
            [.. mandatory, .. selectedOptionalIds],
            optionalCapacity,
            selectedOptionalIds.Length,
            optionalCandidates.Count - selectedOptionalIds.Length);
    }

    private static ArgumentException Duplicate(long poiId, string parameterName) =>
        new($"Matrix candidates contain duplicate POI ID {poiId}.", parameterName);
}