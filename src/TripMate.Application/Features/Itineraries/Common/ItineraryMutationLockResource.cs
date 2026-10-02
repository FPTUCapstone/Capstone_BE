using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Itineraries.Common;

/// <summary>
/// Resolves the stable resource shared by every version of a generated itinerary.
/// </summary>
public static class ItineraryMutationLockResource
{
    public static async Task<Result<long>> ResolveAsync(
        IApplicationDbContext dbContext,
        long itineraryId,
        CancellationToken cancellationToken)
    {
        var resourceId = await dbContext.Itineraries
            .AsNoTracking()
            .Where(itinerary => itinerary.Id == itineraryId)
            .Select(itinerary => itinerary.SchedulingRequestId ?? itinerary.Id)
            .SingleOrDefaultAsync(cancellationToken);

        return resourceId == 0
            ? Result.Failure<long>(ItineraryErrorCodes.NotFound, "The itinerary was not found.")
            : Result.Success(resourceId);
    }
}