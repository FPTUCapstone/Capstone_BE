namespace TripMate.Application.Features.Scheduling.Common;

public sealed class SchedulingGenerationOptions
{
    public const string SectionName = "SchedulingGeneration";

    public int TransitionBufferMinutes { get; init; } = 10;

    public int FinalReturnBufferMinutes { get; init; } = 15;
}
