using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripAlerts.WeatherAdvisories;

public sealed class GetWeatherAdvisoriesQueryHandler(
    IApplicationDbContext dbContext,
    ITripWeatherProvider weatherProvider,
    IDateTimeProvider clock,
    IOptions<TripAlertOptions> options)
    : IRequestHandler<GetWeatherAdvisoriesQuery, Result<IReadOnlyCollection<WeatherAdvisoryResponse>>>
{
    private readonly WeatherAlertPolicy policy = new(options);

    public async Task<Result<IReadOnlyCollection<WeatherAdvisoryResponse>>> Handle(
        GetWeatherAdvisoriesQuery request,
        CancellationToken cancellationToken)
    {
        var session = await dbContext.TripSessions.AsNoTracking()
            .Include(candidate => candidate.Items)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.SessionId, cancellationToken);
        if (session is null)
        {
            return Result.Failure<IReadOnlyCollection<WeatherAdvisoryResponse>>(
                TripAlertErrorCodes.NotFound, "The navigation session was not found.");
        }

        if (session.TravelerUserId != request.TravelerUserId)
        {
            return Result.Failure<IReadOnlyCollection<WeatherAdvisoryResponse>>(
                TripAlertErrorCodes.AccessDenied, "You do not have access to these advisories.");
        }

        var now = clock.UtcNow;
        if (session.EndedAtUtc.HasValue || (session.ExpiresAtUtc.HasValue && session.ExpiresAtUtc <= now))
        {
            return Result.Failure<IReadOnlyCollection<WeatherAdvisoryResponse>>(
                TripAlertErrorCodes.SessionCompleted, "The navigation session is no longer open.");
        }

        var pending = session.Items.Where(item => item.Status == TripSessionItem.PendingStatus
            && item.PlannedArrivalUtc >= now
            && item.PlannedArrivalUtc <= now.AddHours(3)).ToArray();
        var poiIds = pending.Select(item => item.PoiId).Distinct().ToArray();
        var poiKinds = await dbContext.PointsOfInterest.AsNoTracking()
            .Where(poi => poiIds.Contains(poi.Id))
            .ToDictionaryAsync(poi => poi.Id,
                poi => new { poi.IndoorOutdoor, poi.IndoorOutdoorReviewedAtUtc }, cancellationToken);
        var persistedItemIds = await dbContext.Incidents.AsNoTracking()
            .Where(incident => incident.SessionId == session.Id
                && incident.IncidentType == Incident.WeatherAdvisoryType
                && incident.ResolvedAtUtc == null
                && incident.AffectedItineraryItemId.HasValue)
            .Select(incident => incident.AffectedItineraryItemId!.Value)
            .ToArrayAsync(cancellationToken);
        var results = new List<WeatherAdvisoryResponse>();
        foreach (var item in pending.Where(item =>
                     poiKinds.TryGetValue(item.PoiId, out var kind)
                     && kind.IndoorOutdoor == IndoorOutdoorType.Outdoor
                     && kind.IndoorOutdoorReviewedAtUtc.HasValue
                     && !persistedItemIds.Contains(item.ItineraryItemId)))
        {
            TripWeatherSnapshot? weather;
            try
            {
                weather = await weatherProvider.GetAsync(
                    new WeatherForecastRequest(item.Latitude, item.Longitude, item.PlannedArrivalUtc),
                    cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (weather is null
                || policy.Decide(weather.RainMmPerHour, weather.Thunderstorm,
                    IndoorOutdoorType.Outdoor, true, item.PlannedArrivalUtc - now, weather.Basis)
                    is not (WeatherAlertAction.TransientAdvisory or WeatherAlertAction.PersistedAdvisory))
            {
                continue;
            }

            results.Add(new WeatherAdvisoryResponse(
                $"rain-moderate:{item.ItineraryItemId}:{item.PlannedArrivalUtc:O}",
                WeatherAdvisoryResponse.InfoSeverity,
                WeatherAdvisoryResponse.ModerateRainMessageCode,
                item.ItineraryItemId,
                item.PoiId,
                item.PoiName,
                item.PlannedArrivalUtc,
                weather.ForecastAtUtc,
                weather.RainMmPerHour,
                weather.Condition));
        }

        return Result.Success<IReadOnlyCollection<WeatherAdvisoryResponse>>(results);
    }
}