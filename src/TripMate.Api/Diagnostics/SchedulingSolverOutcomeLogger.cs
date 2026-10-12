using System.Diagnostics;

using TripMate.Application.Features.Scheduling.Common;

namespace TripMate.Api.Diagnostics;

/// <summary>
/// Writes one structured log line per <c>SchedulingSolverOutcome</c> activity, so operators can monitor the CSP solver
/// from the existing logs (UC-10 Phase 3): the share of wall-clock fallbacks, the CSP solve time, and how often the
/// CSP only confirmed the heuristic plan. Nothing else listens to this activity source, so without this listener the
/// outcome tags are never recorded. The tags are aggregate by design (R8), so the line carries no user or POI identity.
/// </summary>
public sealed class SchedulingSolverOutcomeLogger(ILogger<SchedulingSolverOutcomeLogger> logger) : IHostedService, IDisposable
{
    private ActivityListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SchedulingSolverDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Log,
        };
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    private void Log(Activity activity)
    {
        if (activity.OperationName != SchedulingSolverDiagnostics.OutcomeActivityName)
        {
            return;
        }

        var elapsedMs = activity.GetTagItem("csp.elapsed_ms") is double milliseconds ? Math.Round(milliseconds, 1) : (double?)null;
        logger.LogInformation(
            "Scheduling solver outcome {SolverOutcome} (kind {SolverKind}, mode {SolverMode}): CSP elapsed {CspElapsedMs} ms, "
                + "time limit reached {CspTimeLimitReached}, returned heuristic plan {CspReturnedHeuristicPlan}, nodes {CspNodesExpanded}.",
            activity.GetTagItem("solver.outcome"),
            activity.GetTagItem("solver.kind"),
            activity.GetTagItem("solver.mode"),
            elapsedMs,
            activity.GetTagItem("csp.time_limit_reached"),
            activity.GetTagItem("csp.returned_heuristic_plan"),
            activity.GetTagItem("csp.nodes_expanded"));
    }
}