namespace TripMate.Domain.Entities;

/// <summary>Weather event entity in trip.WeatherEvents (UC-59 read model; UC-70 provider evidence for Operator review).</summary>
public sealed class WeatherEvent
{
    public const string SeverityLow = "Low";
    public const string SeverityModerate = "Moderate";
    public const string SeveritySevere = "Severe";
    public const string SeverityExtreme = "Extreme";

    public const int ProviderMaxLength = 50;
    public const int ProviderEventIdMaxLength = 150;
    public const int EventTypeMaxLength = 40;
    public const int OriginalAuthorityMaxLength = 200;
    public const int VerifiedDirectiveMaxLength = 50;
    public const int DescriptionMaxLength = 1000;

    private WeatherEvent()
    {
    }

    public long Id { get; private set; }

    public string? RegionName { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public string Severity { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public DateTimeOffset ValidFromUtc { get; private set; }

    public DateTimeOffset? ValidToUtc { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public DateTimeOffset IngestedAtUtc { get; private set; }

    // Provider evidence used to create Operator weather-safety reviews.
    public string? Provider { get; private set; }

    public string? ProviderEventId { get; private set; }

    public string? OriginalAuthority { get; private set; }

    public int? OfficialVietnamRiskLevel { get; private set; }

    public string? VerifiedDirective { get; private set; }

    public bool IsWithdrawn { get; private set; }

    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    public static WeatherEvent CreateOfficial(
        string provider,
        string providerEventId,
        string eventType,
        string severity,
        DateTimeOffset validFromUtc,
        DateTimeOffset? validToUtc,
        string? description,
        string? originalAuthority,
        int? officialVietnamRiskLevel,
        string? verifiedDirective,
        DateTimeOffset ingestedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(severity);

        return new WeatherEvent
        {
            Provider = provider.Trim(),
            ProviderEventId = providerEventId.Trim(),
            Source = provider.Trim(),
            EventType = eventType.Trim(),
            Severity = severity.Trim(),
            ValidFromUtc = validFromUtc.ToUniversalTime(),
            ValidToUtc = validToUtc?.ToUniversalTime(),
            Description = NormalizeDescription(description),
            OriginalAuthority = originalAuthority?.Trim(),
            OfficialVietnamRiskLevel = officialVietnamRiskLevel,
            VerifiedDirective = verifiedDirective?.Trim(),
            IngestedAtUtc = ingestedAtUtc.ToUniversalTime(),
            UpdatedAtUtc = ingestedAtUtc.ToUniversalTime(),
            IsWithdrawn = false,
        };
    }

    public void UpdateRevision(
        DateTimeOffset validFromUtc,
        DateTimeOffset? validToUtc,
        string severity,
        string? description,
        int? officialVietnamRiskLevel,
        string? verifiedDirective,
        DateTimeOffset updatedAtUtc)
    {
        ValidFromUtc = validFromUtc.ToUniversalTime();
        ValidToUtc = validToUtc?.ToUniversalTime();
        Severity = severity.Trim();
        if (!string.IsNullOrWhiteSpace(description))
        {
            Description = NormalizeDescription(description);
        }
        OfficialVietnamRiskLevel = officialVietnamRiskLevel;
        VerifiedDirective = verifiedDirective?.Trim();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public void MarkWithdrawn(DateTimeOffset updatedAtUtc)
    {
        IsWithdrawn = true;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        string normalized = description.Trim();
        return normalized.Length <= DescriptionMaxLength
            ? normalized
            : normalized[..DescriptionMaxLength];
    }
}
