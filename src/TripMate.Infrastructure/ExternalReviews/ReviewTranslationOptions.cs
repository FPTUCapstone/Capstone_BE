namespace TripMate.Infrastructure.ExternalReviews;

/// <summary>On-demand machine translation of Tripadvisor review text with Gemini.</summary>
public sealed class ReviewTranslationOptions
{
    public const string SectionName = "ReviewTranslation";

    public bool Enabled { get; init; }

    public string Endpoint { get; init; } = "https://generativelanguage.googleapis.com/v1/interactions";

    public string ModelName { get; init; } = "gemini-3.5-flash-lite";

    public string ApiKey { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 15;

    public int CacheMinutes { get; init; } = 24 * 60;
}