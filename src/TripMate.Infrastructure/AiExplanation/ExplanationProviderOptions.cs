namespace TripMate.Infrastructure.AiExplanation;

public sealed class ExplanationProviderOptions
{
    public const string SectionName = "AiExplanation";

    public bool Enabled { get; init; } = false;

    public string? Endpoint { get; init; }

    public string? ApiKey { get; init; }

    public string? ModelName { get; init; }
}