using TripMate.Application.Features.Weather.Emergency;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Weather.Matching;

public sealed record WeatherDepartureCandidate(
    TourSchedule Schedule,
    WeatherDepartureImpact Impact);

public interface IWeatherAffectedDepartureFinder
{
    Task<IReadOnlyList<WeatherDepartureCandidate>> FindAffectedDeparturesAsync(
        NormalizedWeatherEvent weatherEvent,
        CancellationToken cancellationToken);
}