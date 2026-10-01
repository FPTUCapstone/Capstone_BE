using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class PublishGroupLocationCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock)
    : IRequestHandler<PublishGroupLocationCommand, Result<GroupLocationResponse>>
{
    public Task<Result<GroupLocationResponse>> Handle(
        PublishGroupLocationCommand request,
        CancellationToken cancellationToken) =>
        dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            var member = await dbContext.GroupMembers.AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.GroupId == request.GroupId
                    && candidate.UserId == request.TravelerUserId
                    && candidate.Status == GroupMemberStatus.Active, token);
            if (member is null)
            {
                var exists = await dbContext.TravelGroups.AsNoTracking()
                    .AnyAsync(group => group.Id == request.GroupId, token);
                return Result.Failure<GroupLocationResponse>(
                    exists ? TravelGroupErrorCodes.ActiveMembershipRequired : TravelGroupErrorCodes.GroupNotFound,
                    exists ? "You must be an active group member." : "The travel group could not be found.");
            }

            if (!member.LocationSharingEnabled)
            {
                return Result.Failure<GroupLocationResponse>(
                    TravelGroupErrorCodes.LocationSharingNotEnabled,
                    "Enable group location sharing before sending a position.");
            }

            var currentVersion = member.LocationSharingUpdatedAtUtc?.UtcTicks
                .ToString(CultureInfo.InvariantCulture);
            if (currentVersion is null || !string.Equals(currentVersion, request.SessionVersion, StringComparison.Ordinal))
            {
                return Result.Failure<GroupLocationResponse>(
                    TravelGroupErrorCodes.LocationSharingSessionExpired,
                    "This location-sharing session has ended. Refresh before sending another position.");
            }

            var oldLocations = await dbContext.GroupLocations
                .Where(location => location.GroupId == request.GroupId
                    && location.UserId == request.TravelerUserId)
                .ToListAsync(token);
            dbContext.GroupLocations.RemoveRange(oldLocations);

            var now = clock.UtcNow;
            var location = GroupLocation.Create(
                request.GroupId, request.TravelerUserId, request.Latitude, request.Longitude, now);
            dbContext.GroupLocations.Add(location);
            await dbContext.SaveChangesAsync(token);

            return Result.Success(new GroupLocationResponse(
                location.UserId, location.Latitude, location.Longitude, location.RecordedAtUtc));
        }, cancellationToken);
}