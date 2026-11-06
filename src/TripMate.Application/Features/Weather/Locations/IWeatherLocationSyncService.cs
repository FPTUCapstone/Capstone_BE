namespace TripMate.Application.Features.Weather.Locations;

public sealed record WeatherLocationSyncResult(
    int LocationsCreated,
    int LocationsUpdated,
    int LocationsDeactivated,
    int LocationsLinked);

public interface IWeatherLocationSyncService
{
    Task<WeatherLocationSyncResult> SyncUpcomingLocationsAsync(CancellationToken cancellationToken);
}
