namespace TripMate.Application.Features.Scheduling.Common;

public sealed class SchedulingGenerationOptions
{
    public const string SectionName = "SchedulingGeneration";
    public const int DefaultMaxMatrixCandidates = 40;
    public const int MinimumMaxMatrixCandidates = 6;

    public const int DefaultMaxRouteOptimizationSeeds = 3;
    public const int MinRouteOptimizationSeeds = 1;
    public const int MaxAllowedRouteOptimizationSeeds = 10;

    public const int DefaultMaxRouteEvaluations = 5000;
    public const int MinRouteEvaluations = 100;
    public const int MaxAllowedRouteEvaluations = 50000;

    public int TransitionBufferMinutes { get; init; } = 10;

    public int FinalReturnBufferMinutes { get; init; } = 15;

    public int MaxMatrixCandidates { get; init; } = DefaultMaxMatrixCandidates;

    public bool EnableOptionalRouteOptimization { get; init; } = true;

    public int MaxRouteOptimizationSeeds { get; init; } = DefaultMaxRouteOptimizationSeeds;

    public int MaxRouteEvaluations { get; init; } = DefaultMaxRouteEvaluations;

    public int EffectiveMaxMatrixCandidates => MaxMatrixCandidates >= MinimumMaxMatrixCandidates
        ? MaxMatrixCandidates
        : DefaultMaxMatrixCandidates;
}