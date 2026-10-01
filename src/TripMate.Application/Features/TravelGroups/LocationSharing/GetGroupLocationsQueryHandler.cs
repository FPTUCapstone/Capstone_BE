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
                && member.Status == GroupMemberStatus.Active, cancellationToken);
        if (!viewerIsMember)
        {
            var exists = await dbContext.TravelGroups.AsNoTracking()
                .AnyAsync(group => group.Id == request.GroupId, cancellationToken);
            return Result.Failure<GroupLocationsResponse>(
                exists ? TravelGroupErrorCodes.ActiveMembershipRequired : TravelGroupErrorCodes.GroupNotFound,
                exists ? "You must be an active group member." : "The travel group could not be found.");
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
                    && viewer.Status == GroupMemberStatus.Active)
                && dbContext.GroupMembers.Any(member => member.GroupId == request.GroupId
                    && member.UserId == location.UserId
                    && member.Status == GroupMemberStatus.Active
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