using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TripMate.Application.Features.Scheduling.Diagnostics;

internal static class CandidatePoolFunnelTelemetry
{
    public const string MeterName = "TripMate.Scheduling.CandidatePoolFunnel";
    public const string ActivitySourceName = MeterName;
    public const string AttemptCountName = "tripmate.scheduling.funnel.attempts";
    public const string StageCandidateCountName = "tripmate.scheduling.funnel.candidates";
    public const string DropCountName = "tripmate.scheduling.funnel.dropped";
    public const string RouteMatrixPointCountName = "tripmate.scheduling.funnel.matrix.points";
    public const string RouteMatrixElementCountName = "tripmate.scheduling.funnel.matrix.elements";
    public const string FinalOptionalCountName = "tripmate.scheduling.funnel.plan.optional";
    public const string StageDurationName = "tripmate.scheduling.funnel.stage.duration";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    private static readonly Counter<long> AttemptCounter = Meter.CreateCounter<long>(AttemptCountName);
    private static readonly Histogram<long> CandidateHistogram =
        Meter.CreateHistogram<long>(StageCandidateCountName);
    private static readonly Histogram<long> DropHistogram =
        Meter.CreateHistogram<long>(DropCountName);
    private static readonly Histogram<long> MatrixPointHistogram =
        Meter.CreateHistogram<long>(RouteMatrixPointCountName);
    private static readonly Histogram<long> MatrixElementHistogram =
        Meter.CreateHistogram<long>(RouteMatrixElementCountName);
    private static readonly Histogram<long> FinalOptionalHistogram =
        Meter.CreateHistogram<long>(FinalOptionalCountName);
    private static readonly Histogram<double> StageDurationHistogram =
        Meter.CreateHistogram<double>(StageDurationName, "ms");

    public static void Record(CandidatePoolFunnelDiagnostics diagnostics, string outcome)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!IsOutcome(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        try
        {
            var stageTags = new TagList
            {
                { "attempt", diagnostics.Attempt },
                { "outcome", outcome },
            };
            AttemptCounter.Add(1, stageTags);
            RecordCandidateStages(diagnostics, stageTags);
            RecordDrops(diagnostics);
            RecordFinalCounts(diagnostics, stageTags);

            if (diagnostics.RouteMatrixPoints is int points
                && diagnostics.TransportMode is string transportMode)
            {
                var matrixTags = new TagList
                {
                    { "transport_mode", transportMode },
                    { "attempt", diagnostics.Attempt },
                };
                MatrixPointHistogram.Record(points, matrixTags);
                MatrixElementHistogram.Record((long)points * points, matrixTags);
            }

            foreach ((string stage, double duration) in diagnostics.StageDurationsMilliseconds)
            {
                var tags = new TagList
                {
                    { "stage", stage },
                    { "outcome", outcome },
                };
                StageDurationHistogram.Record(duration, tags);
            }

            using Activity? activity = ActivitySource.StartActivity("candidate-pool-funnel");
            if (activity is not null)
            {
                activity.SetTag("tripmate.scheduling.funnel.attempt", diagnostics.Attempt);
                activity.SetTag("tripmate.scheduling.funnel.outcome", outcome);
                SetCountTags(activity, diagnostics);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // Observability is best-effort and must never alter scheduling behavior.
        }
    }

    private static void RecordCandidateStages(
        CandidatePoolFunnelDiagnostics diagnostics,
        TagList stageTags)
    {
        RecordStage(CandidatePoolFunnelDimensions.StageEligibleOptional, diagnostics.EligibleOptional);
        RecordStage(CandidatePoolFunnelDimensions.StageProviderPool, diagnostics.ProviderPool);
        RecordStage(
            CandidatePoolFunnelDimensions.StageMatrixOptionalCapacity,
            diagnostics.MatrixOptionalCapacity);
        RecordStage(CandidatePoolFunnelDimensions.StageMatrixOptional, diagnostics.MatrixOptional);
        RecordStage(
            CandidatePoolFunnelDimensions.StagePlannedOptionalVisits,
            diagnostics.PlannedOptionalVisits);
        RecordStage(CandidatePoolFunnelDimensions.StagePlannedRestPois, diagnostics.PlannedRestPois);
        RecordStage(
            CandidatePoolFunnelDimensions.StageFinalizeValidFrozenPool,
            diagnostics.FinalizeValidFrozenPool);
        return;

        void RecordStage(string stage, int? value)
        {
            if (!value.HasValue)
            {
                return;
            }

            var tags = stageTags;
            tags.Add("stage", stage);
            CandidateHistogram.Record(value.Value, tags);
        }
    }

    private static void RecordDrops(
        CandidatePoolFunnelDiagnostics diagnostics)
    {
        RecordDrop(
            "provider_cap",
            Difference(diagnostics.EligibleOptional, diagnostics.ProviderPool));
        RecordDrop(
            "matrix_cap",
            Difference(diagnostics.ProviderPool, diagnostics.MatrixOptional));
        RecordDrop(
            "finalize_invalidation",
            Difference(diagnostics.ProviderPool, diagnostics.FinalizeValidFrozenPool));
        return;

        void RecordDrop(string reason, int? value)
        {
            if (!value.HasValue)
            {
                return;
            }

            var tags = new TagList
            {
                { "reason", reason },
                { "attempt", diagnostics.Attempt },
            };
            DropHistogram.Record(value.Value, tags);
        }
    }

    private static void RecordFinalCounts(
        CandidatePoolFunnelDiagnostics diagnostics,
        TagList stageTags)
    {
        Record("visit", diagnostics.PlannedOptionalVisits);
        Record("rest", diagnostics.PlannedRestPois);
        return;

        void Record(string kind, int? value)
        {
            if (!value.HasValue)
            {
                return;
            }

            var tags = stageTags;
            tags.Add("kind", kind);
            FinalOptionalHistogram.Record(value.Value, tags);
        }
    }

    private static int? Difference(int? from, int? to) =>
        from.HasValue && to.HasValue ? Math.Max(0, from.Value - to.Value) : null;

    private static void SetCountTags(
        Activity activity,
        CandidatePoolFunnelDiagnostics diagnostics)
    {
        activity.SetTag("tripmate.scheduling.funnel.eligible_optional", diagnostics.EligibleOptional);
        activity.SetTag("tripmate.scheduling.funnel.provider_pool", diagnostics.ProviderPool);
        activity.SetTag("tripmate.scheduling.funnel.matrix_optional", diagnostics.MatrixOptional);
        activity.SetTag(
            "tripmate.scheduling.funnel.finalize_valid_frozen_pool",
            diagnostics.FinalizeValidFrozenPool);
        activity.SetTag(
            "tripmate.scheduling.funnel.planned_optional_visits",
            diagnostics.PlannedOptionalVisits);
    }

    private static bool IsOutcome(string outcome) => outcome is
        CandidatePoolFunnelDimensions.OutcomeSuccess
        or CandidatePoolFunnelDimensions.OutcomeInfeasible
        or CandidatePoolFunnelDimensions.OutcomeRoutingFailure
        or CandidatePoolFunnelDimensions.OutcomeCancelled
        or CandidatePoolFunnelDimensions.OutcomeSnapshotMismatchRetryable
        or CandidatePoolFunnelDimensions.OutcomeSnapshotMismatchTerminal
        or CandidatePoolFunnelDimensions.OutcomeLostOwnership;
}