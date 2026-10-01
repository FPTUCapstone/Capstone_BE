using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class GetEnabledLocationSharingGroupsQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetEnabledLocationSharingGroupsQuery, Result<EnabledLocationSharingGroupsResponse>>
{
    public async Task<Result<EnabledLocationSharingGroupsResponse>> Handle(
        GetEnabledLocationSharingGroupsQuery request,
        CancellationToken cancellationToken)
    {
        var groupIds = await dbContext.GroupMembers.AsNoTracking()
            .Where(member => member.UserId == request.TravelerUserId
                && member.Status == GroupMemberStatus.Active
                && member.LocationSharingEnabled)
            .OrderBy(member => member.GroupId)
            .Select(member => member.GroupId)
            .ToArrayAsync(cancellationToken);
        return Result.Success(new EnabledLocationSharingGroupsResponse(groupIds));
    }
}