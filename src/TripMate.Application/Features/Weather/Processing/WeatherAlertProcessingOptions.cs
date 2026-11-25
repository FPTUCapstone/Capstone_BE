namespace TripMate.Application.Features.Weather.Processing;

public class WeatherAlertProcessingOptions
{
    public const string SectionName = "TomorrowIo";

    public bool Enabled { get; set; }
    public int SafetyReviewAcknowledgementSlaMinutes { get; set; } = 30;
    public int LocationMonitoringLookaheadDays { get; set; } = 7;

    /// <summary>
    /// Pre-departure checkpoints (hours before <c>TourSchedule.StartAtUtc</c>) at which open
    /// CustomInsight reviews are re-evaluated against Tomorrow.io hourly forecasts.
    /// </summary>
    public int[] ForecastCheckpointHoursBeforeDeparture { get; set; } = [24, 12, 6];

    /// <summary>Thresholds matching the production Tomorrow.io Custom Insight (metric units).</summary>
    public double SevereRainMmPerHour { get; set; } = 10.0;
    public double SevereWindSpeedMetersPerSecond { get; set; } = 10.8;
    public double SevereWindGustMetersPerSecond { get; set; } = 15.0;
    public double LowVisibilityKm { get; set; } = 1.0;
    public int ThunderstormWeatherCode { get; set; } = 8000;
}