using FluentAssertions;

using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Itineraries;

public sealed class FixedOrderDiagnosisTests
{
    // 08:00 in Da Nang on Tuesday 2026-10-20.
    private static readonly DateTimeOffset StartAtUtc = new(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);

    private static readonly GenerationOpeningHours[] AllTuesday =
        [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))];

    [Fact]
    public async Task ExplainAsync_StopClosedWhenReached_ReportsClosedAtArrivalWithClosingTime()
    {
        var museum = Candidate(1, 60, 0m, AllTuesday);
        var market = Candidate(2, 60, 0m, [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(9, 30))]);

        var result = await Diagnose(Input(480, null, museum, market), [1, 2]);

        result.Should().NotBeNull();
        result!.PoiId.Should().Be(2);
        result.Reason.Should().Be(StopInfeasibilityReasons.ClosedAtArrival);
        result.ClosesAt.Should().Be(new TimeOnly(9, 30));
        result.PlannedArrival.Should().NotBeNull();
    }

    [Fact]
    public async Task ExplainAsync_StopPastTheEndOfTheTrip_ReportsExceedsEndTimeWithOverflow()
    {
        var museum = Candidate(1, 60, 0m, AllTuesday);
        var beach = Candidate(2, 60, 0m, AllTuesday);

        var result = await Diagnose(Input(120, null, museum, beach), [1, 2]);

        result.Should().NotBeNull();
        result!.PoiId.Should().Be(2);
        result.Reason.Should().Be(StopInfeasibilityReasons.ExceedsEndTime);
        result.OverflowMinutes.Should().BePositive();
    }

    [Fact]
    public async Task ExplainAsync_StopOverTheBudget_ReportsExceedsBudgetWithOverflow()
    {
        var museum = Candidate(1, 60, 30_000m, AllTuesday);
        var theme = Candidate(2, 60, 30_000m, AllTuesday);

        var result = await Diagnose(Input(480, 50_000m, museum, theme), [1, 2]);

        result.Should().NotBeNull();
        result!.PoiId.Should().Be(2);
        result.Reason.Should().Be(StopInfeasibilityReasons.ExceedsBudget);
        result.OverflowVnd.Should().Be(10_000m);
    }

    [Fact]
    public async Task ExplainAsync_FeasibleOrder_ReturnsNull()
    {
        var museum = Candidate(1, 60, 0m, AllTuesday);
        var beach = Candidate(2, 60, 0m, AllTuesday);

        var result = await Diagnose(Input(480, null, museum, beach), [1, 2]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExplainAsync_FitsWithElasticCompression_ReturnsNull()
    {
        // 60m + 60m + 80m (transit + buffers) = 200m without compression.
        // With 180m available, 25% compression yields 45m + 45m + 80m = 170m <= 180m.
        var museum = Candidate(1, 60, 0m, AllTuesday);
        var beach = Candidate(2, 60, 0m, AllTuesday);

        var result = await Diagnose(Input(180, null, museum, beach), [1, 2]);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExplainAsync_Preserves30MinuteFloorForAttractions()
    {
        // 30m + 30m + 45m transit = 105m.
        // With 100m available, neither can be reduced below 30m floor.
        var museum = Candidate(1, 30, 0m, AllTuesday);
        var beach = Candidate(2, 30, 0m, AllTuesday);

        var result = await Diagnose(Input(100, null, museum, beach), [1, 2]);

        result.Should().NotBeNull();
        result!.Reason.Should().Be(StopInfeasibilityReasons.ExceedsEndTime);
    }

    private static Task<StopInfeasibility?> Diagnose(GenerationInput input, long[] order) =>
        new FixedOrderDiagnosis(new FixedProvider()).ExplainAsync(input, order, CancellationToken.None);

    private static GenerationInput Input(int availableMinutes, decimal? budget, params GenerationCandidate[] candidates) =>
        new(
            StartAtUtc,
            TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"),
            new RoutePoint(16.0544m, 108.2022m),
            new RoutePoint(16.0544m, 108.2022m),
            availableMinutes,
            TransportMode.Motorbike,
            RestPreference.None,
            budget,
            candidates,
            []);

    private static GenerationCandidate Candidate(long id, int visitMinutes, decimal cost, GenerationOpeningHours[] hours) =>
        new(
            id,
            $"Place {id}",
            new RoutePoint(16m + (id / 1_000m), 108m + (id / 1_000m)),
            visitMinutes,
            cost,
            hours,
            0m,
            0m,
            null,
            null,
            null);

    private sealed class FixedProvider : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken)
        {
            var minutes = new int[points.Count, points.Count];
            for (var row = 0; row < points.Count; row++)
            {
                for (var column = 0; column < points.Count; column++)
                {
                    minutes[row, column] = row == column ? 0 : 15;
                }
            }

            return Task.FromResult(RouteDurationMatrix.Create(minutes));
        }
    }
}