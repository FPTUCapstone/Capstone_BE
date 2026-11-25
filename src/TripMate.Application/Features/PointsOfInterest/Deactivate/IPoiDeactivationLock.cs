namespace TripMate.Application.Features.PointsOfInterest.Deactivate;

/// <summary>
/// Serializes POI deactivation and coordinates locking with TM-225 provisioning.
/// </summary>
public interface IPoiDeactivationLock
{
    Task AcquireAsync(long poiId, CancellationToken cancellationToken);
}