using TripMate.Application.Features.Weather.Processing;

namespace TripMate.Infrastructure.Weather;

public sealed class TomorrowIoOptions : WeatherAlertProcessingOptions
{
    public string WebhookSecret { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The Alert created and approved on the Tomorrow.io dashboard. TripMate links its Locations
    /// to this Alert and accepts notifications from it only; it never creates Insights or Alerts.
    /// </summary>
    public string AlertId { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.tomorrow.io";
    public int WebhookToleranceSeconds { get; set; } = 300;
    public int MaxPayloadSizeBytes { get; set; } = 1_048_576;
    public int ReconciliationIntervalMinutes { get; set; } = 15;
    public int ReconciliationLookbackMinutes { get; set; } = 120;
    public int LocationSyncIntervalHours { get; set; } = 6;
}