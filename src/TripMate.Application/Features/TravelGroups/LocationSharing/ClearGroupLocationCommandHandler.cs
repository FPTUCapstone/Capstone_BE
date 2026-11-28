using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class ClearGroupLocationCommandHandler(IApplicationDbContext dbContext, IDateTimeProvider clock)
    : IRequestHandler<ClearGroupLocationCommand, Result>
{
    public Task<Result> Handle(ClearGroupLocationCommand request, CancellationToken cancellationToken) =>
        dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            var member = await dbContext.GroupMembers
                .FirstOrDefaultAsync(candidate => candidate.GroupId == request.GroupId
                    && candidate.UserId == request.TravelerUserId
                    && candidate.Status == GroupMemberStatus.Active
                && candidate.User.Status == AccountStatus.Active
                && candidate.TravelGroup.Status == TravelGroupStatus.Active, token);
            if (member is null)
            {
                var groupStatus = await dbContext.TravelGroups.AsNoTracking().Where(group => group.Id == request.GroupId).Select(group => (TravelGroupStatus?)group.Status).SingleOrDefaultAsync(token);
                return Result.Failure(
                    groupStatus is null ? TravelGroupErrorCodes.GroupNotFound : groupStatus != TravelGroupStatus.Active ? TravelGroupErrorCodes.GroupClosed : TravelGroupErrorCodes.ActiveMembershipRequired,
                    groupStatus is null ? "The travel group could not be found." : groupStatus != TravelGroupStatus.Active ? "This group is closed; location sharing is unavailable." : "You must be an active group member.");
            }

            if (member.LocationSharingEnabled)
            {
                member.InvalidateLocationSession(clock.UtcNow);
            }

            var locations = await dbContext.GroupLocations
                .Where(location => location.GroupId == request.GroupId
                    && location.UserId == request.TravelerUserId)
                .ToListAsync(token);
            dbContext.GroupLocations.RemoveRange(locations);
            await dbContext.SaveChangesAsync(token);
            return Result.Success();
        }, cancellationToken);
}