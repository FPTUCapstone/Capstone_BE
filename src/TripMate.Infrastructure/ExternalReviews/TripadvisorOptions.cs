namespace TripMate.Infrastructure.ExternalReviews;

/// <summary>Tripadvisor Terra content API. The key stays on the backend; clients never see it.</summary>
public sealed class TripadvisorOptions
{
    public const string SectionName = "Tripadvisor";

    /// <summary>Off by default so development and tests never spend the shared quota.</summary>
    public bool Enabled { get; init; }

    public string BaseUrl { get; init; } = "https://terra.tripadvisor.com/api/";

    public string ApiKey { get; init; } = string.Empty;

    /// <summary>How long ratings and reviews are reused before Terra is asked again.</summary>
    public int CacheMinutes { get; init; } = 720;

    /// <summary>
    /// Billed units per UTC day (Terra bills multi-location reads per location returned). Requests beyond it
    /// return empty results instead of calling Terra.
    /// </summary>
    public int DailyCallBudget { get; init; } = 40;

    public int TimeoutSeconds { get; init; } = 8;
}