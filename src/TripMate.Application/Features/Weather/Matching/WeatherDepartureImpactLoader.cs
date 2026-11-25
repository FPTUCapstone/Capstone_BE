using TripMate.Application.Features.Weather.Emergency;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Weather.Matching;

public sealed class WeatherDepartureImpactLoader : IWeatherDepartureImpactLoader
{
    public WeatherDepartureImpact LoadImpact(TourSchedule schedule, IReadOnlySet<long>? passedPoiIds = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        WeatherPoint? meetingPoint = null;
        if (schedule.MeetingLatitude.HasValue && schedule.MeetingLongitude.HasValue)
        {
            meetingPoint = new WeatherPoint(schedule.MeetingLatitude.Value, schedule.MeetingLongitude.Value);
        }

        TimeZoneInfo? timeZone = null;
        string tzId = (schedule.Tour?.TimeZoneId ?? Tour.DefaultTimeZoneId).Trim();
        TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out timeZone);
        DateTime? localDepartureDate = timeZone is not null
            ? TimeZoneInfo.ConvertTime(schedule.StartAtUtc, timeZone).Date
            : null;

        var stops = new List<WeatherTourStop>();
        if (schedule.Tour?.ItineraryItems is not null)
        {
            foreach (var item in schedule.Tour.ItineraryItems.OrderBy(i => i.DayNo).ThenBy(i => i.SequenceNo))
            {
                if (item.Poi is not null)
                {
                    var (arrivalUtc, departureUtc) = ResolvePlannedWindowUtc(item, timeZone, localDepartureDate);
                    bool passed = passedPoiIds?.Contains(item.PoiId) == true;
                    stops.Add(new WeatherTourStop(
                        Point: new WeatherPoint(item.Poi.Latitude, item.Poi.Longitude),
                        Required: item.IsRequiredForWeather ?? true,
                        PlannedVisitAtUtc: arrivalUtc,
                        Passed: passed,
                        PlannedDepartureAtUtc: departureUtc));
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

    private static (DateTimeOffset? ArrivalUtc, DateTimeOffset? DepartureUtc) ResolvePlannedWindowUtc(
        TourItineraryItem item,
        TimeZoneInfo? timeZone,
        DateTime? localDepartureDate)
    {
        if (!item.PlannedTime.HasValue || timeZone is null || !localDepartureDate.HasValue)
        {
            return (null, null);
        }

        DateTime localArrival = DateTime.SpecifyKind(
            localDepartureDate.Value
                .AddDays(Math.Max(0, item.DayNo - 1))
                .Add(item.PlannedTime.Value.ToTimeSpan()),
            DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(localArrival) || timeZone.IsAmbiguousTime(localArrival))
        {
            return (null, null);
        }

        DateTimeOffset arrivalUtc = new(TimeZoneInfo.ConvertTimeToUtc(localArrival, timeZone), TimeSpan.Zero);
        DateTimeOffset departureUtc = arrivalUtc.AddMinutes(Math.Max(0, item.StayDurationMinutes));
        return (arrivalUtc, departureUtc);
    }
}