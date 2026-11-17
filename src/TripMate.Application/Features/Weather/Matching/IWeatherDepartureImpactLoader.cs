using TripMate.Application.Features.Weather.Emergency;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Weather.Matching;

public interface IWeatherDepartureImpactLoader
{
    WeatherDepartureImpact LoadImpact(TourSchedule schedule);
}