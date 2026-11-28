using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class GetGroupLocationsQueryHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock)
    : IRequestHandler<GetGroupLocationsQuery, Result<GroupLocationsResponse>>
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(2);

    public async Task<Result<GroupLocationsResponse>> Handle(
        GetGroupLocationsQuery request,
        CancellationToken cancellationToken)
    {
        var viewerIsMember = await dbContext.GroupMembers.AsNoTracking()
            .AnyAsync(member => member.GroupId == request.GroupId
                && member.UserId == request.TravelerUserId
                && member.Status == GroupMemberStatus.Active
                && member.User.Status == AccountStatus.Active
                && member.TravelGroup.Status == TravelGroupStatus.Active, cancellationToken);
        if (!viewerIsMember)
        {
            var groupStatus = await dbContext.TravelGroups.AsNoTracking().Where(group => group.Id == request.GroupId).Select(group => (TravelGroupStatus?)group.Status).SingleOrDefaultAsync(cancellationToken);
            return Result.Failure<GroupLocationsResponse>(
                groupStatus is null ? TravelGroupErrorCodes.GroupNotFound : groupStatus != TravelGroupStatus.Active ? TravelGroupErrorCodes.GroupClosed : TravelGroupErrorCodes.ActiveMembershipRequired,
                groupStatus is null ? "The travel group could not be found." : groupStatus != TravelGroupStatus.Active ? "This group is closed; location sharing is unavailable." : "You must be an active group member.");
        }

        var cutoff = clock.UtcNow - FreshnessWindow;
        var candidates = await dbContext.GroupLocations.AsNoTracking()
            .Where(location => location.GroupId == request.GroupId
                && location.RecordedAtUtc >= cutoff
                // Re-check the viewer in the same SQL statement that reads a
                // coordinate. A membership removed after the authorization
                // query cannot receive a stale location in the next query.
                && dbContext.GroupMembers.Any(viewer => viewer.GroupId == request.GroupId
                    && viewer.UserId == request.TravelerUserId
                    && viewer.Status == GroupMemberStatus.Active
                && viewer.User.Status == AccountStatus.Active
                && viewer.TravelGroup.Status == TravelGroupStatus.Active)
                && dbContext.GroupMembers.Any(member => member.GroupId == request.GroupId
                    && member.UserId == location.UserId
                    && member.Status == GroupMemberStatus.Active
                && member.User.Status == AccountStatus.Active
                && member.TravelGroup.Status == TravelGroupStatus.Active
                    && member.LocationSharingEnabled
                    && member.LocationSharingUpdatedAtUtc.HasValue
                    && location.RecordedAtUtc >= member.LocationSharingUpdatedAtUtc.Value))
            .OrderByDescending(location => location.RecordedAtUtc)
            .ThenByDescending(location => location.Id)
            .ToListAsync(cancellationToken);
        var locations = candidates
            .GroupBy(location => location.UserId)
            .Select(group => group.First())
            .OrderBy(location => location.UserId)
            .Select(location => new GroupLocationResponse(
                location.UserId, location.Latitude, location.Longitude, location.RecordedAtUtc))
            .ToArray();
        return Result.Success(new GroupLocationsResponse(request.GroupId, locations));
    }
}