using TripMate.Application.Features.Weather.Emergency;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Weather.Matching;

public sealed class WeatherDepartureImpactLoader : IWeatherDepartureImpactLoader
{
    public WeatherDepartureImpact LoadImpact(TourSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        WeatherPoint? meetingPoint = null;
        if (schedule.MeetingLatitude.HasValue && schedule.MeetingLongitude.HasValue)
        {
            meetingPoint = new WeatherPoint(schedule.MeetingLatitude.Value, schedule.MeetingLongitude.Value);
        }

        var stops = new List<WeatherTourStop>();
        if (schedule.Tour?.ItineraryItems is not null)
        {
            foreach (var item in schedule.Tour.ItineraryItems)
            {
                if (item.Poi is not null)
                {
                    stops.Add(new WeatherTourStop(
                        Point: new WeatherPoint(item.Poi.Latitude, item.Poi.Longitude),
                        Required: item.IsRequiredForWeather ?? true,
                        PlannedVisitAtUtc: null,
                        Passed: false));
                }
            }
        }

        bool dataReviewed = meetingPoint is not null || stops.Count > 0;

        return new WeatherDepartureImpact(
            StartAtUtc: schedule.StartAtUtc,
            EndAtUtc: schedule.EndAtUtc,
            MeetingPoint: meetingPoint,
            RequiredStops: stops,
            DataReviewed: dataReviewed);
    }
}