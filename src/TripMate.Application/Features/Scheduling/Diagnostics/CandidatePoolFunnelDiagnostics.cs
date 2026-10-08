using System.Diagnostics;

namespace TripMate.Application.Features.Scheduling.Diagnostics;

internal static class CandidatePoolFunnelDimensions
{
    public const string AttemptInitial = "initial";
    public const string AttemptSnapshotRetry = "snapshot_retry";

    public const string OutcomeSuccess = "success";
    public const string OutcomeInfeasible = "infeasible";
    public const string OutcomeRoutingFailure = "routing_failure";
    public const string OutcomeCancelled = "cancelled";
    public const string OutcomeSnapshotMismatchRetryable = "snapshot_mismatch_retryable";
    public const string OutcomeSnapshotMismatchTerminal = "snapshot_mismatch_terminal";
    public const string OutcomeLostOwnership = "lost_ownership";
    public const string OutcomeUnexpectedFailure = "unexpected_failure";

    public const string StageEligibleOptional = "eligible_optional";
    public const string StageProviderPool = "provider_pool";
    public const string StageMatrixOptionalCapacity = "matrix_optional_capacity";
    public const string StageMatrixOptional = "matrix_optional";
    public const string StagePlannedOptionalVisits = "planned_optional_visits";
    public const string StagePlannedRestPois = "planned_rest_pois";
    public const string StageFinalizeValidFrozenPool = "finalize_valid_frozen_pool";

    public const string StageRanking = "ranking";
    public const string StageSelection = "selection";
    public const string StageMatrix = "matrix";
    public const string StageGeneration = "generation";
    public const string StageFinalizeObservation = "finalize_observation";
}

internal sealed class CandidatePoolFunnelDiagnostics(string attempt)
{
    private readonly Dictionary<string, double> _stageDurationsMilliseconds = [];
    private int _emitted;

    public string Attempt { get; } = attempt is
        CandidatePoolFunnelDimensions.AttemptInitial
        or CandidatePoolFunnelDimensions.AttemptSnapshotRetry
            ? attempt
            : throw new ArgumentOutOfRangeException(nameof(attempt));

    public int? EligibleOptional { get; private set; }
    public int? ProviderPool { get; private set; }
    public int? MatrixOptionalCapacity { get; private set; }
    public int? MatrixOptional { get; private set; }
    public int? RouteMatrixPoints { get; private set; }
    public string? TransportMode { get; private set; }
    public int? PlannedOptionalVisits { get; private set; }
    public int? PlannedRestPois { get; private set; }
    public int? FinalizeValidFrozenPool { get; private set; }

    public IReadOnlyDictionary<string, double> StageDurationsMilliseconds =>
        _stageDurationsMilliseconds;

    public void SetEligibleOptional(int value) => EligibleOptional = NonNegative(value);

    public void SetProviderPool(int value) => ProviderPool = NonNegative(value);

    public void SetMatrixSelection(
        int optionalCapacity,
        int selectedOptional,
        int routeMatrixPoints,
        string transportMode)
    {
        MatrixOptionalCapacity = NonNegative(optionalCapacity);
        MatrixOptional = NonNegative(selectedOptional);
        RouteMatrixPoints = NonNegative(routeMatrixPoints);
        TransportMode = string.IsNullOrWhiteSpace(transportMode)
            ? throw new ArgumentException("Transport mode is required.", nameof(transportMode))
            : transportMode;
    }

    public void SetPreparationCounts(
        int eligibleOptional,
        int providerPool,
        int matrixOptionalCapacity,
        int matrixOptional,
        int routeMatrixPoints,
        string transportMode)
    {
        SetEligibleOptional(eligibleOptional);
        SetProviderPool(providerPool);
        SetMatrixSelection(
            matrixOptionalCapacity,
            matrixOptional,
            routeMatrixPoints,
            transportMode);
    }

    public void SetPlanCounts(int optionalVisits, int restPois)
    {
        PlannedOptionalVisits = NonNegative(optionalVisits);
        PlannedRestPois = NonNegative(restPois);
    }

    public void SetFinalizeValidFrozenPool(int value) =>
        FinalizeValidFrozenPool = NonNegative(value);

    public void RecordStageDuration(string stage, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        _stageDurationsMilliseconds[stage] = Math.Max(0d, duration.TotalMilliseconds);
    }

    public void Emit(string outcome)
    {
        if (Interlocked.Exchange(ref _emitted, 1) != 0)
        {
            return;
        }

        CandidatePoolFunnelTelemetry.Record(this, outcome);
    }

    private static int NonNegative(int value) => value >= 0
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value));
}