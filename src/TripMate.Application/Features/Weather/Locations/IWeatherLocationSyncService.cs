namespace TripMate.Application.Features.Weather.Locations;

public sealed record WeatherLocationSyncResult(
    int LocationsCreated,
    int LocationsUpdated,
    int LocationsDeactivated);

public interface IWeatherLocationSyncService
{
    Task<WeatherLocationSyncResult> SyncUpcomingLocationsAsync(CancellationToken cancellationToken);
}
