using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.Common;

public sealed class ItineraryAccessService(IApplicationDbContext dbContext)
    : IItineraryAccessService
{
    public async Task<Result<ItineraryAccess>> ResolveCurrentAsync(
        long itineraryId,
        long travelerUserId,
        bool trackCurrent,
        CancellationToken cancellationToken)
    {
        var requested = await dbContext.Itineraries
            .AsNoTracking()
            .Where(itinerary => itinerary.Id == itineraryId)
            .Select(itinerary => new
            {
                itinerary.Id,
                itinerary.TravelerUserId,
                itinerary.SchedulingRequestId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (requested is null)
        {
            return Result.Failure<ItineraryAccess>(
                ItineraryErrorCodes.NotFound,
                "The itinerary was not found.");
        }

        var isOwner = requested.TravelerUserId == travelerUserId;
        var isGroupMember = requested.SchedulingRequestId.HasValue
            && await dbContext.TravelGroups.AnyAsync(
                group => group.Itinerary.SchedulingRequestId == requested.SchedulingRequestId
                    && group.GroupMembers.Any(member =>
                        member.UserId == travelerUserId
                        && member.Status == GroupMemberStatus.Active),
                cancellationToken);

        if (!isOwner && !isGroupMember)
        {
            return Result.Failure<ItineraryAccess>(
                ItineraryErrorCodes.AccessDenied,
                "You do not have access to this itinerary.");
        }

        if (!requested.SchedulingRequestId.HasValue)
        {
            var manual = await LoadCurrentAsync(itineraryId, trackCurrent, cancellationToken);
            return manual is null
                ? Result.Failure<ItineraryAccess>(ItineraryErrorCodes.NotFound, "The itinerary was not found.")
                : Result.Success(new ItineraryAccess(
                    isOwner ? ItineraryDetailAccessKind.Owner : ItineraryDetailAccessKind.GroupMember,
                    itineraryId,
                    0,
                    manual,
                    isOwner));
        }

        var current = await dbContext.Itineraries
            .Where(itinerary => itinerary.SchedulingRequestId == requested.SchedulingRequestId)
            .OrderByDescending(itinerary => itinerary.Version)
            .ThenByDescending(itinerary => itinerary.Id)
            .AsTracking(trackCurrent ? QueryTrackingBehavior.TrackAll : QueryTrackingBehavior.NoTracking)
            .FirstOrDefaultAsync(cancellationToken);

        if (current is null)
        {
            return Result.Failure<ItineraryAccess>(ItineraryErrorCodes.NotFound, "The itinerary was not found.");
        }

        return Result.Success(new ItineraryAccess(
            isOwner ? ItineraryDetailAccessKind.Owner : ItineraryDetailAccessKind.GroupMember,
            itineraryId,
            requested.SchedulingRequestId.Value,
            current,
            isOwner));
    }

    private async Task<Domain.Entities.Itinerary?> LoadCurrentAsync(
        long itineraryId,
        bool trackCurrent,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Itineraries.Where(itinerary => itinerary.Id == itineraryId);
        return await (trackCurrent ? query : query.AsNoTracking()).SingleOrDefaultAsync(cancellationToken);
    }
}