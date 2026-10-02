using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.GetMembers;

public sealed class GetTravelGroupMembersQueryHandler(
    IApplicationDbContext dbContext,
    ILogger<GetTravelGroupMembersQueryHandler> logger)
    : IRequestHandler<GetTravelGroupMembersQuery, Result<GetTravelGroupMembersResponse>>
{
    public async Task<Result<GetTravelGroupMembersResponse>> Handle(
        GetTravelGroupMembersQuery request,
        CancellationToken cancellationToken)
    {
        var travelGroup = await dbContext.TravelGroups
            .AsNoTracking()
            .Where(travelGroup => travelGroup.Id == request.GroupId
                && travelGroup.GroupMembers.Any(member =>
                    member.UserId == request.TravelerUserId
                    && member.Status == GroupMemberStatus.Active))
            .Select(travelGroup => new
            {
                travelGroup.Id,
                travelGroup.Name,
                travelGroup.ItineraryId,
                Members = travelGroup.GroupMembers
                    .Where(member => member.Status == GroupMemberStatus.Active)
                    .OrderByDescending(member => member.UserId == travelGroup.HostUserId)
                    .ThenBy(member => member.JoinedAtUtc)
                    .ThenBy(member => member.UserId)
                    .Select(member => new TravelGroupMemberResponse(
                        member.UserId,
                        member.User.FullName,
                        member.User.AvatarUrl,
                        member.UserId == travelGroup.HostUserId,
                        member.JoinedAtUtc,
                        member.LocationSharingEnabled))
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (travelGroup is null)
        {
            var groupExists = await dbContext.TravelGroups
                .AsNoTracking()
                .AnyAsync(group => group.Id == request.GroupId, cancellationToken);
            return Result.Failure<GetTravelGroupMembersResponse>(
                groupExists
                    ? TravelGroupErrorCodes.ActiveMembershipRequired
                    : TravelGroupErrorCodes.GroupNotFound,
                groupExists
                    ? "You must be an active member of this travel group to view its members."
                    : "The travel group could not be found.");
        }

        var members = travelGroup.Members;

        if (members.Count(member => member.IsHost) != 1)
        {
            logger.LogError(
                "Travel group {GroupId} has no single active Group Host membership.",
                request.GroupId);
            return Result.Failure<GetTravelGroupMembersResponse>(
                TravelGroupErrorCodes.MemberListInconsistent,
                "Unable to load group members.");
        }

        return Result.Success(
            new GetTravelGroupMembersResponse(
                travelGroup.Id,
                travelGroup.Name,
                travelGroup.ItineraryId,
                members.Count,
                members));
    }
}