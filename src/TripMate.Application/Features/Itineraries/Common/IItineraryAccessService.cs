using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Itineraries.Common;

public interface IItineraryAccessService
{
    Task<Result<ItineraryAccess>> ResolveCurrentAsync(
        long itineraryId,
        long travelerUserId,
        bool trackCurrent,
        CancellationToken cancellationToken);
}