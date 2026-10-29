namespace TripMate.Application.Features.TripAlerts.Common;

public sealed class TripAlertOptions
{
    public const string SectionName = "TripAlerts";

    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ScheduleDelayThreshold { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan DeviationCooldown { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan DeliveryLease { get; init; } = TimeSpan.FromMinutes(2);
    public double AdvisoryRainMmPerHour { get; init; } = 2.5;
    public double SevereRainMmPerHour { get; init; } = 10;
    public double ExtremeRainMmPerHour { get; init; } = 50;
}