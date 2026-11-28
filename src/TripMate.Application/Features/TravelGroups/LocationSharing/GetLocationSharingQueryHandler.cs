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
                && member.Status == GroupMemberStatus.Active
                && member.User.Status == AccountStatus.Active
                && member.TravelGroup.Status == TravelGroupStatus.Active)
            .Select(member => new LocationSharingResponse(
                member.GroupId,
                member.LocationSharingEnabled,
                member.LocationSharingUpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        if (setting is not null)
        {
            return Result.Success(setting);
        }

        var groupStatus = await dbContext.TravelGroups.AsNoTracking().Where(group => group.Id == request.GroupId).Select(group => (TravelGroupStatus?)group.Status).SingleOrDefaultAsync(cancellationToken);
        return Result.Failure<LocationSharingResponse>(
            groupStatus is null ? TravelGroupErrorCodes.GroupNotFound : groupStatus != TravelGroupStatus.Active ? TravelGroupErrorCodes.GroupClosed : TravelGroupErrorCodes.ActiveMembershipRequired,
            groupStatus is null ? "The travel group could not be found." : groupStatus != TravelGroupStatus.Active ? "This group is closed; location sharing is unavailable." : "You must be an active group member.");
    }
}