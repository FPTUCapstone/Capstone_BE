using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public interface IRouteDurationProvider
{
    /// <summary>Whether this provider can evaluate the mode; catalog-backed transit needs stored metrics.</summary>
    bool SupportsTransportMode(TransportMode transportMode) =>
        transportMode is TransportMode.Walking or TransportMode.Motorbike or TransportMode.Car;

    Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken);
}