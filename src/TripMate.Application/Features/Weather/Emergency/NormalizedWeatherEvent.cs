namespace TripMate.Application.Features.Weather.Emergency;

public enum WeatherVerifiedDirective
{
    None = 0,
    TravelProhibition = 1,
    Evacuation = 2,
    Closure = 3,
}

public static class WeatherProviders
{
    public const string TomorrowIo = "TomorrowIo";
}

public static class WeatherAlertErrorCodes
{
    public const string PayloadEmpty = "Weather.PayloadEmpty";
    public const string PayloadMalformed = "Weather.PayloadMalformed";
}

public static class VietnameseWeatherAuthorities
{
    public const string Nchmf = "NCHMF";
    public const string NchmfFullName = "Trung tâm Dự báo Khí tượng Thủy văn Quốc gia";
    public const string DisasterSteeringCommittee = "Ban chỉ đạo Quốc gia về Phòng chống thiên tai";
    public const string DisasterSteeringCommitteeShort = "VNDMS";
    public const string ProvincialPeoplesCommittee = "Ủy ban nhân dân";
    public const string MaritimeAdministration = "Cục Hàng hải Việt Nam";
    public const string CivilAviationAuthority = "Cục Hàng không Việt Nam";
    public const string TestAuthority = "VN_TEST_AUTHORITY";

    public static readonly IReadOnlySet<string> DefaultAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Nchmf,
        NchmfFullName,
        DisasterSteeringCommittee,
        DisasterSteeringCommitteeShort,
        ProvincialPeoplesCommittee,
        MaritimeAdministration,
        CivilAviationAuthority,
        TestAuthority,
    };

    public static bool IsAllowlisted(string? authority, IReadOnlySet<string>? customAllowlist = null)
    {
        if (string.IsNullOrWhiteSpace(authority)) return false;
        var allowlist = customAllowlist ?? DefaultAllowlist;
        return allowlist.Contains(authority) || allowlist.Any(a => authority.Contains(a, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record NormalizedWeatherEvent(
    string Provider,
    string ProviderEventId,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidToUtc,
    IReadOnlyList<WeatherPolygon> Areas,
    DateTimeOffset ReceivedAtUtc)
{
    public string? ProviderNotificationId { get; init; }
    public string? OriginalAuthority { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public int? OfficialVietnamRiskLevel { get; init; }
    public WeatherVerifiedDirective VerifiedDirective { get; init; } = WeatherVerifiedDirective.None;
    public bool IsWithdrawn { get; init; }
    public string? ProviderSeverity { get; init; }
    public bool IsCustomEvent { get; init; }
    public bool IsSamplePayload { get; init; }

    /// <summary>Tomorrow.io Location ids the notification was raised for (data.locations[].id).</summary>
    public IReadOnlyList<string> ProviderLocationIds { get; init; } = [];

    /// <summary>TripMate points behind <see cref="ProviderLocationIds"/>, resolved from the location mappings.</summary>
    public IReadOnlyList<WeatherPoint> MonitoredPoints { get; init; } = [];

    public bool HasSpatialScope => Areas.Count > 0 || MonitoredPoints.Count > 0;

    /// <summary>
    /// A point is covered when it lies in a published area or is itself a monitored Location
    /// the provider reported. Location alerts carry ids, not geometry.
    /// </summary>
    public bool Covers(WeatherPoint point) =>
        MonitoredPoints.Any(monitored => monitored.MonitoringCell == point.MonitoringCell)
        || Areas.Any(area => area.Contains(point));
}

public sealed record WeatherDecisionEvaluation(
    WeatherDecisionOutcome Outcome,
    string ReasonCode,
    string? Details = null);