using FluentAssertions;

using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public sealed class ProviderPoolSelectorTests
{
    [Fact]
    public void Select_ShuffledCandidates_ReturnsCanonicalTopKAndDropCount()
    {
        ProviderPoolSelectionCandidate lower = Candidate(20, 0.4m, scenic: 10m);
        ProviderPoolSelectionCandidate tieHigherId = Candidate(30, 0.8m, scenic: 8m);
        ProviderPoolSelectionCandidate tieLowerId = Candidate(10, 0.8m, scenic: 8m);

        ProviderPoolSelection result = ProviderPoolSelector.Select(
            [lower, tieHigherId, tieLowerId],
            maximumCandidates: 2);

        result.Candidates.Select(candidate => candidate.Candidate.PoiId)
            .Should().Equal(10L, 30L);
        result.DroppedCount.Should().Be(1);
    }

    [Fact]
    public void Select_DuplicatePoiIds_ThrowsControlledArgumentException()
    {
        Action action = () => ProviderPoolSelector.Select(
            [Candidate(10, 0.8m), Candidate(10, 0.7m)],
            maximumCandidates: 2);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*duplicate*10*");
    }

    private static ProviderPoolSelectionCandidate Candidate(
        long id,
        decimal baseScore,
        decimal? scenic = null) =>
        new(
            new PoiRankingInputCandidate(
                id,
                CategoryId: 1,
                $"POI {id}",
                "Culture",
                [],
                scenic,
                PhotoRating: 5m,
                ExplorationDistanceForRanking: 1m,
                EstimatedVisitCostForRanking: 10_000m),
            baseScore);
}