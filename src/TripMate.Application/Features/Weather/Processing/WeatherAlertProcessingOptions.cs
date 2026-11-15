namespace TripMate.Application.Features.Weather.Processing;

public class WeatherAlertProcessingOptions
{
    public const string SectionName = "TomorrowIo";

    public bool Enabled { get; set; }
    public int SafetyReviewAcknowledgementSlaMinutes { get; set; } = 30;
    public int LocationMonitoringLookaheadDays { get; set; } = 7;
}