namespace TripMate.Domain.Entities;

/// <summary>Weather event entity in trip.WeatherEvents (supporting UC-59 read-model and UC-70 official severe-weather automation).</summary>
public sealed class WeatherEvent
{
    public const string SeverityLow = "Low";
    public const string SeverityModerate = "Moderate";
    public const string SeveritySevere = "Severe";
    public const string SeverityExtreme = "Extreme";

    public const int ProviderMaxLength = 50;
    public const int ProviderEventIdMaxLength = 150;
    public const int RevisionMaxLength = 50;
    public const int OriginalAuthorityMaxLength = 200;
    public const int OriginalSourceReferenceMaxLength = 500;
    public const int VerifiedDirectiveMaxLength = 50;
    public const int RawPayloadHashMaxLength = 64;
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

    public string? Revision { get; private set; }

    public string? OriginalAuthority { get; private set; }

    public string? OriginalSourceReference { get; private set; }

    public int? OfficialVietnamRiskLevel { get; private set; }

    public string? VerifiedDirective { get; private set; }

    public bool IsWithdrawn { get; private set; }

    public string? GeometryGeoJson { get; private set; }

    public string? RawPayloadHash { get; private set; }

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
        string? originalSourceReference,
        int? officialVietnamRiskLevel,
        string? verifiedDirective,
        string? geometryGeoJson,
        string? rawPayloadHash,
        DateTimeOffset ingestedAtUtc,
        string? revision = null)
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
            OriginalSourceReference = originalSourceReference?.Trim(),
            OfficialVietnamRiskLevel = officialVietnamRiskLevel,
            VerifiedDirective = verifiedDirective?.Trim(),
            GeometryGeoJson = geometryGeoJson,
            RawPayloadHash = rawPayloadHash?.Trim(),
            IngestedAtUtc = ingestedAtUtc.ToUniversalTime(),
            UpdatedAtUtc = ingestedAtUtc.ToUniversalTime(),
            Revision = revision?.Trim(),
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
        string? geometryGeoJson,
        string? rawPayloadHash,
        DateTimeOffset updatedAtUtc,
        string? revision = null)
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
        if (!string.IsNullOrWhiteSpace(geometryGeoJson))
        {
            GeometryGeoJson = geometryGeoJson;
        }
        RawPayloadHash = rawPayloadHash?.Trim();
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        if (!string.IsNullOrWhiteSpace(revision))
        {
            Revision = revision.Trim();
        }
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
