using FluentAssertions;

using Microsoft.Extensions.Logging;

using TripMate.Api.Diagnostics;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Csp;

namespace TripMate.Api.IntegrationTests.Scheduling;

/// <summary>
/// UC-10 Phase 3 monitoring: nothing else listens to the solver activity source, so these tests pin that each
/// recorded outcome becomes exactly one log line with the aggregate fields operators watch. The tests record outcomes
/// directly, so they run without parallelization to keep other tests' outcomes out of the captured log.
/// </summary>
[Collection(nameof(SchedulingSolverOutcomeLoggerCollection))]
public sealed class SchedulingSolverOutcomeLoggerTests
{
    [Fact]
    public async Task LogsTheTimeLimitFallback_WithTheFieldsOperatorsMonitor()
    {
        var logger = new RecordingLogger<SchedulingSolverOutcomeLogger>();
        using var service = new SchedulingSolverOutcomeLogger(logger);
        await service.StartAsync(CancellationToken.None);

        SchedulingSolverDiagnostics.RecordOutcome(
            SchedulingSolverMode.Csp, SchedulingSolverOutcomes.HeuristicFallbackCspTimeLimit, new CspStatistics());
        await service.StopAsync(CancellationToken.None);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Values["SolverOutcome"].Should().Be(SchedulingSolverOutcomes.HeuristicFallbackCspTimeLimit);
        entry.Values["SolverKind"].Should().Be(SchedulingSolverKinds.CspFallback);
        entry.Values["SolverMode"].Should().Be(nameof(SchedulingSolverMode.Csp));
        entry.Values["CspTimeLimitReached"].Should().Be(false);
        entry.Values["CspReturnedHeuristicPlan"].Should().Be(false);
        entry.Values["CspElapsedMs"].Should().Be(0d);
        entry.Values["CspNodesExpanded"].Should().Be(0);
        entry.Values["CspNodeLimitReached"].Should().Be(false);
        entry.Values["CspInSearchEvaluationLimitReached"].Should().Be(false);
        entry.Values["CspTieEvaluationLimitReached"].Should().Be(false);
        entry.Values["CspEvaluatorCalls"].Should().Be(0);
    }

    /// <summary>
    /// Review Phase 3: the search-limit signals are operationally relevant (a rising share means the node budget or the
    /// evaluator caps are too small for production inputs), so the log line must carry them, not only the Activity.
    /// </summary>
    [Fact]
    public async Task LogsTheSearchLimitSignals()
    {
        var logger = new RecordingLogger<SchedulingSolverOutcomeLogger>();
        using var service = new SchedulingSolverOutcomeLogger(logger);
        await service.StartAsync(CancellationToken.None);

        using (var activity = SchedulingSolverDiagnostics.Source.StartActivity(SchedulingSolverDiagnostics.OutcomeActivityName))
        {
            activity!.SetTag("solver.outcome", SchedulingSolverOutcomes.Csp);
            activity.SetTag("csp.node_limit_reached", true);
            activity.SetTag("csp.in_search_evaluation_limit_reached", true);
            activity.SetTag("csp.tie_evaluation_limit_reached", true);
            activity.SetTag("csp.evaluator_calls", 17);
        }

        await service.StopAsync(CancellationToken.None);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Values["CspNodeLimitReached"].Should().Be(true);
        entry.Values["CspInSearchEvaluationLimitReached"].Should().Be(true);
        entry.Values["CspTieEvaluationLimitReached"].Should().Be(true);
        entry.Values["CspEvaluatorCalls"].Should().Be(17);
    }

    [Fact]
    public async Task LogsTheHeuristicOutcome_WithoutCspFields()
    {
        var logger = new RecordingLogger<SchedulingSolverOutcomeLogger>();
        using var service = new SchedulingSolverOutcomeLogger(logger);
        await service.StartAsync(CancellationToken.None);

        SchedulingSolverDiagnostics.RecordOutcome(SchedulingSolverMode.Heuristic, SchedulingSolverOutcomes.Heuristic, csp: null);
        await service.StopAsync(CancellationToken.None);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Values["SolverOutcome"].Should().Be(SchedulingSolverOutcomes.Heuristic);
        entry.Values["SolverKind"].Should().Be(SchedulingSolverKinds.Heuristic);
        entry.Values["CspElapsedMs"].Should().BeNull();
        entry.Values["CspReturnedHeuristicPlan"].Should().BeNull();
    }

    [Fact]
    public async Task StopsLogging_WhenTheHostStops()
    {
        var logger = new RecordingLogger<SchedulingSolverOutcomeLogger>();
        using var service = new SchedulingSolverOutcomeLogger(logger);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        SchedulingSolverDiagnostics.RecordOutcome(SchedulingSolverMode.Csp, SchedulingSolverOutcomes.Csp, new CspStatistics());

        logger.Entries.Should().BeEmpty();
    }

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
        }
    }
}

[CollectionDefinition(nameof(SchedulingSolverOutcomeLoggerCollection), DisableParallelization = true)]
public sealed class SchedulingSolverOutcomeLoggerCollection;