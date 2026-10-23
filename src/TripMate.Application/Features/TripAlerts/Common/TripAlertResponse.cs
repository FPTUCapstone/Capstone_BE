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
    long? ReroutingProposalId)
{
    public const string WarningSeverity = "Warning";
    public const string SevereWeatherMessageCode = "trip_alert.severe_weather";
    public const string ScheduleDelayMessageCode = "trip_alert.schedule_delay";
    public const string RouteDeviationMessageCode = "trip_alert.route_deviation";
    public const string PoiClosureMessageCode = "trip_alert.poi_closure";

    public static TripAlertResponse From(
        Incident incident,
        Notification notification,
        long? reroutingProposalId) => new(
            incident.Id,
            incident.IncidentType,
            WarningSeverity,
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
            reroutingProposalId);

    private static string ResolveMessageCode(string incidentType) => incidentType switch
    {
        Incident.SevereWeatherType => SevereWeatherMessageCode,
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