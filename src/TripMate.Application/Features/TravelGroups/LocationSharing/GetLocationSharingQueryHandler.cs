using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class GetLocationSharingQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetLocationSharingQuery, Result<LocationSharingResponse>>
{
    public async Task<Result<LocationSharingResponse>> Handle(
        GetLocationSharingQuery request,
        CancellationToken cancellationToken)
    {
        var setting = await dbContext.GroupMembers.AsNoTracking()
            .Where(member => member.GroupId == request.GroupId
                && member.UserId == request.TravelerUserId
                && member.Status == GroupMemberStatus.Active)
            .Select(member => new LocationSharingResponse(
                member.GroupId,
                member.LocationSharingEnabled,
                member.LocationSharingUpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        if (setting is not null)
        {
            return Result.Success(setting);
        }

        var exists = await dbContext.TravelGroups.AsNoTracking()
            .AnyAsync(group => group.Id == request.GroupId, cancellationToken);
        return Result.Failure<LocationSharingResponse>(
            exists ? TravelGroupErrorCodes.ActiveMembershipRequired : TravelGroupErrorCodes.GroupNotFound,
            exists ? "You must be an active group member." : "The travel group could not be found.");
    }
}