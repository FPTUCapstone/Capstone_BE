using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Scheduling.Common;

public interface IRouteDurationProvider
{
    Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken);
}
