using MediatR;

using Microsoft.EntityFrameworkCore;

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
    WeatherAlertClassifier classifier)
    : IRequestHandler<GetWeatherAdvisoriesQuery, Result<IReadOnlyCollection<WeatherAdvisoryResponse>>>
{
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

        var pending = session.Items.Where(item => item.Status == TripSessionItem.PendingStatus).ToArray();
        var poiIds = pending.Select(item => item.PoiId).Distinct().ToArray();
        var poiKinds = await dbContext.PointsOfInterest.AsNoTracking()
            .Where(poi => poiIds.Contains(poi.Id))
            .ToDictionaryAsync(poi => poi.Id, poi => poi.IndoorOutdoor, cancellationToken);
        var results = new List<WeatherAdvisoryResponse>();
        foreach (var item in pending.Where(item =>
                     poiKinds.GetValueOrDefault(item.PoiId) == IndoorOutdoorType.Outdoor))
        {
            TripWeatherSnapshot? weather;
            try
            {
                weather = await weatherProvider.GetAsync(
                    new WeatherForecastRequest(item.Latitude, item.Longitude, item.PlannedArrivalUtc),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            if (weather is null
                || classifier.Classify(weather.RainMmPerHour, weather.Thunderstorm,
                    weather.OfficialAlerts.Count > 0, IndoorOutdoorType.Outdoor)
                != WeatherAlertLevel.Advisory)
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