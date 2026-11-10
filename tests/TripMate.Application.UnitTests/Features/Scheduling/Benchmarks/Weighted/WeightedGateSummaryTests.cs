using FluentAssertions;

using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Benchmarks.Weighted;

/// <summary>ADR-002 (G4b): the WeightedUtility switch gate is computed from measured runs, not judged by hand.</summary>
public sealed class WeightedGateSummaryTests
{
    [Fact]
    public void Create_PassesWhenLatencyStopsAndOrderingAreWithinTheTargets()
    {
        var summary = WeightedGateSummary.Create(
            [Latency(10, 50, 60, 70), Latency(40, 120, 150, 180)],
            [Pace(TravelerPace.Relaxed, 3), Pace(TravelerPace.Moderate, 5), Pace(TravelerPace.Fast, 7)]);

        summary.LatencyPassed.Should().BeTrue();
        summary.NoTimeLimit.Should().BeTrue();
        summary.PaceTargetsPassed.Should().BeTrue();
        summary.PaceOrderPassed.Should().BeTrue();
        summary.Passed.Should().BeTrue();
        summary.LatencyBySize.Select(size => size.CandidateCount).Should().Equal(10, 40);
        summary.LatencyBySize.Single(size => size.CandidateCount == 40).P99Milliseconds.Should().Be(180);
    }

    [Fact]
    public void Create_FailsOnSlowRunsTimeLimitsAndStopCountsOutsideTheTargets()
    {
        var summary = WeightedGateSummary.Create(
            [Latency(20, 250, 400, 450, timeLimit: true)],
            [Pace(TravelerPace.Relaxed, 5), Pace(TravelerPace.Moderate, 4), Pace(TravelerPace.Fast, 3)]);

        summary.LatencyPassed.Should().BeFalse("the 20-candidate p95 is above 300 ms");
        summary.NoTimeLimit.Should().BeFalse();
        summary.PaceTargetsPassed.Should().BeFalse("Relaxed 5 and Fast 3 are outside 2–4 and 6–8");
        summary.PaceOrderPassed.Should().BeFalse("stops must not decrease from Relaxed to Fast");
        summary.Passed.Should().BeFalse();
    }

    /// <summary>A fast majority must not hide a slow candidate size.</summary>
    [Fact]
    public void Create_FailsLatencyWhenAnyCandidateSizeIsSlow_EvenIfTheOverallP95IsFast()
    {
        WeightedGateRun[] fast = Enumerable.Range(0, 39).Select(_ => Latency(10, 20, 20, 20)).ToArray();

        var summary = WeightedGateSummary.Create(
            [.. fast, Latency(20, 400, 420, 440)],
            [Pace(TravelerPace.Moderate, 5)]);

        summary.P95Milliseconds.Should().BeLessThanOrEqualTo(300);
        summary.LatencyPassed.Should().BeFalse("the 20-candidate p95 is above 300 ms");
    }

    /// <summary>
    /// ADR-002 amendment (2026-11-11): 40 candidates may take up to the 800 ms rollback threshold, because the slow tail
    /// is dense mandatory input and a smaller node budget costs plan quality.
    /// </summary>
    [Theory]
    [InlineData(10, 300)]
    [InlineData(20, 300)]
    [InlineData(40, 800)]
    public void P95Limit_IsThreeHundredMillisecondsBelowFortyCandidates_AndTheRollbackThresholdAtForty(int candidates, double limit) =>
        WeightedGateSummary.P95LimitMilliseconds(candidates).Should().Be(limit);

    [Fact]
    public void Create_AcceptsFortyCandidatesUpToTheRollbackThreshold_AndFailsAboveIt()
    {
        WeightedGateSummary.Create([Latency(40, 500, 520, 540)], [Pace(TravelerPace.Moderate, 5)])
            .LatencyPassed.Should().BeTrue("520 ms is within 800 ms at 40 candidates");
        WeightedGateSummary.Create([Latency(40, 700, 810, 850)], [Pace(TravelerPace.Moderate, 5)])
            .LatencyPassed.Should().BeFalse("the 40-candidate p95 is above 800 ms");
    }

    /// <summary>ADR-002 §4.4: by J, the served plan is never worse than the heuristic plan or the greedy incumbent.</summary>
    [Fact]
    public void Create_FailsWhenTheServedPlanScoresBelowTheHeuristicOrTheGreedyPlan()
    {
        var latency = Latency(10, 10, 10, 10);
        var summary = WeightedGateSummary.Create(
            [
                latency with { ModelObjective = 100, HeuristicObjective = 90, GreedyObjective = 100 },
                latency with { ModelObjective = 100, HeuristicObjective = 101, GreedyObjective = 50 },
                latency with { ModelObjective = 100, HeuristicObjective = 50, GreedyObjective = 120 },
            ],
            [Pace(TravelerPace.Moderate, 5)]);

        summary.DominanceRuns.Should().Be(3);
        summary.DominanceViolations.Should().Be(2);
        summary.NotWorseThanBaselines.Should().BeFalse();
        summary.Passed.Should().BeFalse();
    }

    [Fact]
    public void Create_PassesDominanceWhenEveryServedPlanScoresAtLeastBothBaselines()
    {
        var latency = Latency(10, 10, 10, 10);
        var summary = WeightedGateSummary.Create(
            [latency with { ModelObjective = 100, HeuristicObjective = 100, GreedyObjective = 99 }],
            [Pace(TravelerPace.Relaxed, 3), Pace(TravelerPace.Moderate, 5), Pace(TravelerPace.Fast, 7)]);

        summary.DominanceViolations.Should().Be(0);
        summary.NotWorseThanBaselines.Should().BeTrue();
        summary.Passed.Should().BeTrue();
    }

    /// <summary>PLAN G4: travelers with different interests get different optional stops (mean pairwise Jaccard &lt; 0.4).</summary>
    [Fact]
    public void Create_FailsWhenPersonasShareMostOptionalStopsAtAPace()
    {
        var summary = WeightedGateSummary.Create(
            [Latency(10, 10, 10, 10)],
            [
                Pace(TravelerPace.Relaxed, 3) with { PersonaId = "A", OptionalVisitPoiIds = [5, 8] },
                Pace(TravelerPace.Relaxed, 3) with { PersonaId = "B", OptionalVisitPoiIds = [5, 8] },
                Pace(TravelerPace.Relaxed, 3) with { PersonaId = "C", OptionalVisitPoiIds = [12, 13] },
                Pace(TravelerPace.Moderate, 5) with { PersonaId = "A", OptionalVisitPoiIds = [1, 2, 3, 4] },
                Pace(TravelerPace.Moderate, 5) with { PersonaId = "B", OptionalVisitPoiIds = [5, 6, 7, 8] },
            ]);

        var relaxed = summary.OverlapByPace.Single(pace => pace.Pace == TravelerPace.Relaxed);
        relaxed.MeanJaccard.Should().BeApproximately(1.0 / 3, 1e-9, "pairs AB = 1, AC = 0, BC = 0");
        relaxed.MaxJaccard.Should().Be(1);
        summary.OverlapByPace.Single(pace => pace.Pace == TravelerPace.Moderate).MeanJaccard.Should().Be(0);
        summary.PersonasDistinct.Should().BeTrue("both means are below 0.4");

        var shared = WeightedGateSummary.Create(
            [Latency(10, 10, 10, 10)],
            [
                Pace(TravelerPace.Relaxed, 3) with { PersonaId = "A", OptionalVisitPoiIds = [5, 8] },
                Pace(TravelerPace.Relaxed, 3) with { PersonaId = "B", OptionalVisitPoiIds = [5, 8] },
            ]);
        shared.PersonasDistinct.Should().BeFalse();
        shared.Passed.Should().BeFalse();
    }

    [Fact]
    public void Create_AveragesStopsPerPaceOverFeasibleRunsOnly()
    {
        var summary = WeightedGateSummary.Create(
            [Latency(10, 10, 10, 10)],
            [Pace(TravelerPace.Moderate, 4), Pace(TravelerPace.Moderate, 6), Pace(TravelerPace.Moderate, 0, feasible: false)]);

        var moderate = summary.StopsByPace.Single(pace => pace.Pace == TravelerPace.Moderate);
        moderate.MeanStops.Should().Be(5);
        moderate.Runs.Should().Be(2);
        moderate.InfeasibleRuns.Should().Be(1);
    }

    private static WeightedGateRun Latency(int candidates, params double[] samples) =>
        Latency(candidates, samples, timeLimit: false);

    private static WeightedGateRun Latency(int candidates, double a, double b, double c, bool timeLimit) =>
        Latency(candidates, [a, b, c], timeLimit);

    private static WeightedGateRun Latency(int candidates, double[] samples, bool timeLimit) =>
        new($"grid-c{candidates}", candidates, "Persona", TravelerPace.Moderate, true, 4, null, null, null,
            timeLimit ? "heuristic_fallback_csp_time_limit" : "csp", !timeLimit, false, timeLimit, 1000, 0, 0, samples);

    private static WeightedGateRun Pace(TravelerPace pace, int stops, bool feasible = true) =>
        new("danang", 19, "Persona", pace, feasible, stops, null, null, null, "csp", true, false, false, 1000, 0, 0, [10]);
}