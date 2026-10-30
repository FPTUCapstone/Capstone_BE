using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripAlerts.Common;

public sealed record TripAlertResponse(
    long IncidentId,
    string Type,
    string Severity,
    string MessageCode,
    string? Description,
    TripAlertAffectedItemResponse? AffectedItem,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    bool IsRead,
    long? ReroutingProposalId,
    string? WeatherBasis = null,
    DateTimeOffset? WeatherAtUtc = null,
    double? RainIntensityMmPerHour = null,
    bool CanSkipAffectedStop = false,
    string? WeatherCondition = null)
{
    public const string InfoSeverity = "Info";
    public const string WarningSeverity = "Warning";
    public const string WeatherAdvisoryMessageCode = "trip_alert.weather_advisory";
    public const string SevereWeatherMessageCode = "trip_alert.severe_weather";
    public const string ScheduleDelayMessageCode = "trip_alert.schedule_delay";
    public const string RouteDeviationMessageCode = "trip_alert.route_deviation";
    public const string PoiClosureMessageCode = "trip_alert.poi_closure";

    public static TripAlertResponse From(
        Incident incident,
        Notification notification,
        long? reroutingProposalId,
        bool reviewedOutdoor = false) => new(
            incident.Id,
            incident.IncidentType,
            incident.IncidentType == Incident.WeatherAdvisoryType ? InfoSeverity : WarningSeverity,
            ResolveMessageCode(incident.IncidentType),
            incident.Description,
            incident.AffectedItem is null
                ? null
                : new TripAlertAffectedItemResponse(
                    incident.AffectedItem.ItineraryItemId,
                    incident.AffectedItem.PoiId,
                    incident.AffectedItem.PoiName,
                    incident.AffectedItem.PlannedArrivalUtc),
            incident.DetectedAtUtc,
            incident.ResolvedAtUtc,
            notification.Status == NotificationStatus.Read,
            reroutingProposalId,
            incident.WeatherBasis,
            incident.WeatherAtUtc,
            incident.RainIntensityMmPerHour,
            incident.IncidentType == Incident.SevereWeatherType
                && incident.ResolvedAtUtc is null
                && reviewedOutdoor,
            ResolveWeatherCondition(incident.SourceKey));

    private static string? ResolveWeatherCondition(string? sourceKey)
    {
        if (sourceKey is null) return null;
        if (sourceKey.StartsWith(WeatherAlertPolicy.ModerateRainSourcePrefix, StringComparison.Ordinal))
            return "ModerateRain";
        if (sourceKey.StartsWith(WeatherAlertPolicy.HeavyRainSourcePrefix, StringComparison.Ordinal))
            return "HeavyRain";
        if (sourceKey.StartsWith(WeatherAlertPolicy.ExtremeRainSourcePrefix, StringComparison.Ordinal))
            return "ExtremeRain";
        if (sourceKey.StartsWith(WeatherAlertPolicy.ThunderstormSourcePrefix, StringComparison.Ordinal))
            return "Thunderstorm";
        return null;
    }

    private static string ResolveMessageCode(string incidentType) => incidentType switch
    {
        Incident.SevereWeatherType => SevereWeatherMessageCode,
        Incident.WeatherAdvisoryType => WeatherAdvisoryMessageCode,
        Incident.ScheduleDelayType => ScheduleDelayMessageCode,
        Incident.RouteDeviationType => RouteDeviationMessageCode,
        Incident.PoiClosureType => PoiClosureMessageCode,
        _ => throw new InvalidOperationException($"Unsupported trip alert type '{incidentType}'."),
    };
}

public sealed record TripAlertAffectedItemResponse(
    long ItemId,
    long PoiId,
    string Name,
    DateTimeOffset PlannedArrivalUtc);