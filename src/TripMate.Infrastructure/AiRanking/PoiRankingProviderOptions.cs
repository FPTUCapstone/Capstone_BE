namespace TripMate.Infrastructure.AiRanking;

public sealed class PoiRankingProviderOptions
{
    public const string SectionName = "AiRanking";

    public bool Enabled { get; init; } = false;

    public string? Endpoint { get; init; }

    public string? ApiKey { get; init; }

    public string? ModelName { get; init; }
}