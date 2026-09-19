using System.Globalization;

namespace TripMate.Application.Features.Admin.SystemConfigs.Common;

/// <summary>
/// The four algorithm configuration rows managed by UC-57 (SRS §3.9.5). Other
/// dbo.SystemConfigs rows belong to their owning use cases and are never touched here.
/// </summary>
public static class AlgorithmParameterDefinitions
{
    public const string BufferTimeMinutesKey = "CSP.BufferTimeMinutes";

    public const string DefaultTravelSpeedKmhKey = "CSP.DefaultTravelSpeedKmh";

    public const string ReroutingSearchRadiusKmKey = "Rerouting.SearchRadiusKm";

    public const string WeatherAlertThresholdSeverityKey = "Weather.AlertThresholdSeverity";

    public static readonly string[] ManagedKeys =
    [
        BufferTimeMinutesKey,
        DefaultTravelSpeedKmhKey,
        ReroutingSearchRadiusKmKey,
        WeatherAlertThresholdSeverityKey,
    ];

    // Seeded defaults in tripmate_schema_v7.sql, used when a row is missing.
    public const int BufferTimeMinutesDefault = 15;

    public const double DefaultTravelSpeedKmhDefault = 30;

    public const double ReroutingSearchRadiusKmDefault = 5;

    public const string WeatherAlertThresholdSeverityDefault = "Severe";

    public const string InvalidValueMessage =
        "Parameter value out of allowed range (e.g., buffer time must be 5-60 mins).";

    public static readonly string[] AllowedSeverities = ["Moderate", "Severe", "Extreme"];

    public static int ParseBufferTimeMinutes(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
        parsed is >= 5 and <= 60
            ? parsed
            : BufferTimeMinutesDefault;

    public static double ParseTravelSpeed(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
        parsed is >= 10 and <= 120
            ? parsed
            : DefaultTravelSpeedKmhDefault;

    public static double ParseSearchRadius(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
        parsed is >= 1 and <= 50
            ? parsed
            : ReroutingSearchRadiusKmDefault;

    public static string ParseSeverity(string? value) =>
        TryNormalizeSeverity(value, out var canonical) ? canonical : WeatherAlertThresholdSeverityDefault;

    public static bool TryNormalizeSeverity(string? value, out string canonical)
    {
        canonical = WeatherAlertThresholdSeverityDefault;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var severity in AllowedSeverities)
        {
            if (string.Equals(severity, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                canonical = severity;
                return true;
            }
        }

        return false;
    }
}