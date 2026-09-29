namespace TripMate.Application.Features.Scheduling.Common;

public sealed class SchedulingGenerationOptions
{
    public const string SectionName = "SchedulingGeneration";
    public const int DefaultMaxMatrixCandidates = 40;
    public const int MinimumMaxMatrixCandidates = 6;

    public int TransitionBufferMinutes { get; init; } = 10;

    public int FinalReturnBufferMinutes { get; init; } = 15;

    public int MaxMatrixCandidates { get; init; } = DefaultMaxMatrixCandidates;

    public int EffectiveMaxMatrixCandidates => MaxMatrixCandidates >= MinimumMaxMatrixCandidates
        ? MaxMatrixCandidates
        : DefaultMaxMatrixCandidates;
}