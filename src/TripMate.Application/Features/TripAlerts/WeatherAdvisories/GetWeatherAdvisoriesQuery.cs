using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.TripAlerts.WeatherAdvisories;

public sealed record GetWeatherAdvisoriesQuery(long SessionId, long TravelerUserId)
    : IRequest<Result<IReadOnlyCollection<WeatherAdvisoryResponse>>>;

public sealed record WeatherAdvisoryResponse(
    string AdvisoryKey,
    string Severity,
    string MessageCode,
    long ItemId,
    long PoiId,
    string PoiName,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset ForecastAtUtc,
    double RainMmPerHour,
    string? Condition)
{
    public const string InfoSeverity = "Info";
    public const string ModerateRainMessageCode = "trip_alert.weather_advisory";
}