using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class UpdateLocationSharingCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock)
    : IRequestHandler<UpdateLocationSharingCommand, Result<LocationSharingResponse>>
{
    public Task<Result<LocationSharingResponse>> Handle(
        UpdateLocationSharingCommand request,
        CancellationToken cancellationToken) =>
        dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            var member = await dbContext.GroupMembers
                .FirstOrDefaultAsync(candidate => candidate.GroupId == request.GroupId
                    && candidate.UserId == request.TravelerUserId
                    && candidate.Status == GroupMemberStatus.Active, token);
            if (member is null)
            {
                var exists = await dbContext.TravelGroups.AsNoTracking()
                    .AnyAsync(group => group.Id == request.GroupId, token);
                return Result.Failure<LocationSharingResponse>(
                    exists ? TravelGroupErrorCodes.ActiveMembershipRequired : TravelGroupErrorCodes.GroupNotFound,
                    exists ? "You must be an active group member." : "The travel group could not be found.");
            }

            member.SetLocationSharing(request.Enabled, clock.UtcNow);
            if (!request.Enabled)
            {
                var oldLocations = await dbContext.GroupLocations
                    .Where(location => location.GroupId == request.GroupId
                        && location.UserId == request.TravelerUserId)
                    .ToListAsync(token);
                dbContext.GroupLocations.RemoveRange(oldLocations);
            }

            await dbContext.SaveChangesAsync(token);
            return Result.Success(new LocationSharingResponse(
                member.GroupId, member.LocationSharingEnabled, member.LocationSharingUpdatedAtUtc));
        }, cancellationToken);
}