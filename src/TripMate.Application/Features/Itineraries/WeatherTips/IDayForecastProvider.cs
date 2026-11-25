namespace TripMate.Application.Features.Itineraries.WeatherTips;

public interface IDayForecastProvider
{
    /// <summary>
    /// The hourly forecast around a location, oldest hour first. Returns <c>null</c> when the integration is off or
    /// the response has no usable hourly timeline; throws <see cref="HttpRequestException"/> when the service fails.
    /// </summary>
    Task<IReadOnlyList<HourlyForecast>?> GetHourlyAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken);
}