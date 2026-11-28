using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.HostManagement;

public sealed class DissolveTravelGroupCommandHandler(
    IApplicationDbContext dbContext,
    IGroupJoinLock groupLock,
    ILogger<DissolveTravelGroupCommandHandler> logger)
    : IRequestHandler<DissolveTravelGroupCommand, Result>
{
    public async Task<Result> Handle(DissolveTravelGroupCommand request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
                {
                    await groupLock.AcquireGroupLockAsync(request.GroupId, token);
                    if (!await dbContext.Users.AsNoTracking().AnyAsync(user =>
                        user.Id == request.HostUserId && user.Status == AccountStatus.Active
                        && user.Role == UserRole.Traveler, token))
                    {
                        return Result.Failure(TravelGroupErrorCodes.HostPermissionRequired,
                            "An Active Traveler account is required to manage this travel group.");
                    }

                    var group = await dbContext.TravelGroups.AsNoTracking()
                        .Where(entity => entity.Id == request.GroupId)
                        .Select(entity => new
                        {
                            entity.HostUserId,
                            entity.Status,
                            entity.ClosedAtUtc,
                            entity.Itinerary.SourceType,
                            HasActiveHost = entity.GroupMembers.Any(member =>
                                member.UserId == entity.HostUserId && member.Status == GroupMemberStatus.Active),
                        })
                        .FirstOrDefaultAsync(token);
                    if (group is null)
                    {
                        return Result.Failure(TravelGroupErrorCodes.GroupNotFound,
                            "The specified travel group does not exist.");
                    }
                    if (group.HostUserId != request.HostUserId)
                    {
                        return Result.Failure(TravelGroupErrorCodes.HostPermissionRequired,
                            "Only the active Host can dissolve this travel group.");
                    }
                    if (group.Status != TravelGroupStatus.Active)
                    {
                        return Result.Failure(TravelGroupErrorCodes.GroupClosed, "This travel group is closed.");
                    }
                    if (group.ClosedAtUtc is not null || !group.HasActiveHost)
                    {
                        logger.LogError("Dissolve found inconsistent Host state for group {GroupId}.", request.GroupId);
                        return Result.Failure(TravelGroupErrorCodes.MemberListInconsistent,
                            "Travel group membership is inconsistent.");
                    }

                    // Close requires no Active members, but history and shared snapshots require
                    // Active membership. Do not bypass the aggregate or silently revoke history.
                    return Result.Failure(TravelGroupErrorCodes.DissolutionRestricted,
                        group.SourceType == Itinerary.BookedTourSourceType
                            ? "This booked-tour travel group cannot be dissolved while preserving history and booking/itinerary access. No booking, itinerary or group data has been changed."
                            : "This travel group cannot be dissolved while preserving history: closure requires ending all memberships, which removes chat history and shared itinerary snapshot access. No group or itinerary data has been changed.");
                }, cancellationToken);
            }
            catch (Exception exception) when (
                exception is DbUpdateConcurrencyException || dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
                if (attempt < 3)
                {
                    logger.LogWarning(exception, "Retrying dissolve check for group {GroupId}; attempt {Attempt}.",
                        request.GroupId, attempt);
                    continue;
                }

                logger.LogError(exception, "Dissolve check exhausted concurrency retries for group {GroupId}.", request.GroupId);
                return Result.Failure(TravelGroupErrorCodes.ConcurrentRetryExhausted,
                    "The group is temporarily busy. Please retry this request.");
            }
        }
    }
}