using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Personalization;

namespace TripMate.Application.UnitTests.Features.Scheduling.Common;

public sealed class MatrixCandidateSelectorTests
{
    [Fact]
    public void Select_ReservesMandatoryCapacityAndUsesFrozenOptionalOrder()
    {
        MatrixCandidateSelection result = MatrixCandidateSelector.Select(
            mandatoryPoiIds: [9, 3],
            optionalCandidates:
            [
                Entry(30, effectiveScore: 0.7m),
                Entry(10, effectiveScore: 0.9m),
                Entry(20, effectiveScore: 0.8m),
            ],
            maximumCandidates: 4);

        result.OrderedPoiIds.Should().Equal(3L, 9L, 10L, 20L);
        result.OptionalCapacity.Should().Be(2);
        result.SelectedOptionalCount.Should().Be(2);
        result.DroppedOptionalCount.Should().Be(1);
    }

    [Fact]
    public void Select_ShuffledEqualCandidates_EndsTieBreakAtPoiId()
    {
        PoiRankingSnapshotEntry first = Entry(20, 0.5m);
        PoiRankingSnapshotEntry second = Entry(10, 0.5m);

        MatrixCandidateSelection result = MatrixCandidateSelector.Select(
            mandatoryPoiIds: [],
            optionalCandidates: [first, second],
            maximumCandidates: 2);

        result.OrderedPoiIds.Should().Equal(10L, 20L);
    }

    [Fact]
    public void Select_DuplicateAcrossMandatoryAndOptional_ThrowsControlledArgumentException()
    {
        Action action = () => MatrixCandidateSelector.Select(
            mandatoryPoiIds: [10],
            optionalCandidates: [Entry(10, 0.5m)],
            maximumCandidates: 2);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*duplicate*10*");
    }

    private static PoiRankingSnapshotEntry Entry(long id, decimal effectiveScore) =>
        new(
            id,
            TripMateBaseScore: effectiveScore,
            EffectiveDesirabilityScore: effectiveScore,
            ScenicScoreForRanking: 5m,
            PhotoRatingForRanking: 5m,
            ExplorationDistanceForRanking: 1m,
            EstimatedVisitCostForRanking: 10_000m);
}