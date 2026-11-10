using System.Diagnostics;

using FluentAssertions;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;
using TripMate.Application.UnitTests.Features.Scheduling.Fixtures;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Common;

/// <summary>
/// ADR-002 (G2): every generated plan says which solver produced it and whether the CSP proved its result, so clients can
/// stop presenting fallbacks and node-limited plans as proven CSP optima.
/// </summary>
public sealed class GenerationProvenanceTests
{
    [Fact]
    public async Task CspPlan_RecordsTheCspOutcome_TheObjectiveMode_AndACompletedSearch()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 1, 61);

        var result = await Service(matrix, SchedulingSolverMode.Csp).GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Provenance.Should().Be(new SchedulingProvenance(
            SchedulingSolverOutcomes.Csp, nameof(ObjectiveMode.Lexicographic), SearchCompleted: true));
    }

    [Fact]
    public async Task CspTimeLimit_RecordsTheHeuristicFallback_AsAnIncompleteSearch()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 1, 61);

        var result = await Service(matrix, SchedulingSolverMode.Csp, timeLimitMilliseconds: 0)
            .GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Provenance.Should().Be(new SchedulingProvenance(
            SchedulingSolverOutcomes.HeuristicFallbackCspTimeLimit, nameof(ObjectiveMode.Lexicographic), SearchCompleted: false));
        result.Value.Provenance!.SolverKind.Should().Be(SchedulingSolverKinds.CspFallback);
    }

    [Fact]
    public async Task HeuristicMode_RecordsNoObjectiveAndNoSearch()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 1, 61);

        var result = await Service(matrix, SchedulingSolverMode.Heuristic).GenerateAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Provenance.Should().Be(new SchedulingProvenance(SchedulingSolverOutcomes.Heuristic, null, null));
    }

    /// <summary>
    /// The CSP result is discarded at the time limit (R5), so "no plan" here does not prove the request infeasible: report
    /// a retryable timeout instead of asking the traveler to change the request.
    /// </summary>
    [Fact]
    public async Task CspTimeLimit_WithoutAHeuristicPlan_ReportsARetryableTimeout_NotAnInfeasibleRequest()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 2, 53);
        var closed = input.Candidates.First(candidate => input.MandatoryPoiIds.Contains(candidate.Id));
        input = input with
        {
            Candidates = input.Candidates
                .Select(candidate => candidate.Id == closed.Id ? candidate with { OpeningHours = [] } : candidate)
                .ToArray(),
        };

        using var parent = new Activity(nameof(CspTimeLimit_WithoutAHeuristicPlan_ReportsARetryableTimeout_NotAnInfeasibleRequest)).Start();
        var traceId = parent.TraceId;
        string? outcome = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SchedulingSolverDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.TraceId == traceId
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId && activity.OperationName == SchedulingSolverDiagnostics.OutcomeActivityName)
                {
                    outcome = activity.GetTagItem("solver.outcome") as string;
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var result = await Service(matrix, SchedulingSolverMode.Csp, timeLimitMilliseconds: 0)
            .GenerateAsync(input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(SchedulingErrorCodes.GenerationTimedOut);
        outcome.Should().Be(SchedulingSolverOutcomes.CspTimeLimitWithoutPlan);
    }

    [Fact]
    public async Task CspOutcome_IsRecordedWithTheObjectiveMode()
    {
        var (input, matrix) = OptionalRouteOptimizationScenarios.CreateSyntheticCorpusScenario(10, 1, 61);
        using var parent = new Activity(nameof(CspOutcome_IsRecordedWithTheObjectiveMode)).Start();
        var traceId = parent.TraceId;
        object? objectiveMode = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SchedulingSolverDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.TraceId == traceId
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId && activity.OperationName == SchedulingSolverDiagnostics.OutcomeActivityName)
                {
                    objectiveMode = activity.GetTagItem("solver.objective_mode");
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await Service(matrix, SchedulingSolverMode.Csp).GenerateAsync(input, CancellationToken.None);

        objectiveMode.Should().Be(nameof(ObjectiveMode.Lexicographic));
    }

    private static ItineraryGenerationService Service(
        RouteDurationMatrix matrix,
        SchedulingSolverMode mode,
        int timeLimitMilliseconds = 600_000) =>
        new(new StaticMatrixProvider(matrix), new SchedulingGenerationOptions
        {
            SolverMode = mode,
            Csp = new CspOptions { TimeLimitMilliseconds = timeLimitMilliseconds },
        });

    private sealed class StaticMatrixProvider(RouteDurationMatrix matrix) : IRouteDurationProvider
    {
        public Task<RouteDurationMatrix> GetMatrixAsync(
            IReadOnlyList<RoutePoint> points,
            TransportMode transportMode,
            CancellationToken cancellationToken) => Task.FromResult(matrix);
    }
}