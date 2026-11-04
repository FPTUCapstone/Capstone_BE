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
    public const string Tomorrow = "Tomorrow";
    public const string OpenWeather = "OpenWeather";
    public const string TestFixture = "TestFixture";
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
    public string? Revision { get; init; }
    public string? OriginalAuthority { get; init; }
    public string? OriginalSourceReference { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public int? OfficialVietnamRiskLevel { get; init; }
    public WeatherVerifiedDirective VerifiedDirective { get; init; } = WeatherVerifiedDirective.None;
    public bool IsWithdrawn { get; init; }
    public string? ProviderSeverity { get; init; }
    public string? Certainty { get; init; }
    public string? Urgency { get; init; }
    public bool IsCustomEvent { get; init; }
    public bool IsSamplePayload { get; init; }
}

public sealed record WeatherDecisionEvaluation(
    WeatherDecisionOutcome Outcome,
    string ReasonCode,
    string? Details = null);
