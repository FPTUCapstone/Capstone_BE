using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.HostManagement;

public sealed class RenameTravelGroupCommandHandler(
    IApplicationDbContext dbContext,
    IGroupJoinLock groupLock,
    IDateTimeProvider clock,
    ILogger<RenameTravelGroupCommandHandler> logger)
    : IRequestHandler<RenameTravelGroupCommand, Result<RenameTravelGroupResponse>>
{
    public async Task<Result<RenameTravelGroupResponse>> Handle(
        RenameTravelGroupCommand request, CancellationToken cancellationToken)
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
                        return Result.Failure<RenameTravelGroupResponse>(
                            TravelGroupErrorCodes.HostPermissionRequired,
                            "An Active Traveler account is required to manage this travel group.");
                    }

                    var group = await dbContext.TravelGroups
                        .Include(entity => entity.GroupMembers)
                        .FirstOrDefaultAsync(entity => entity.Id == request.GroupId, token);
                    if (group is null)
                    {
                        return Result.Failure<RenameTravelGroupResponse>(
                            TravelGroupErrorCodes.GroupNotFound, "The specified travel group does not exist.");
                    }
                    if (group.HostUserId != request.HostUserId)
                    {
                        return Result.Failure<RenameTravelGroupResponse>(
                            TravelGroupErrorCodes.HostPermissionRequired, "Only the active Host can rename this travel group.");
                    }
                    if (group.Status != TravelGroupStatus.Active)
                    {
                        return Result.Failure<RenameTravelGroupResponse>(
                            TravelGroupErrorCodes.GroupClosed, "This travel group is closed.");
                    }
                    if (group.ClosedAtUtc is not null || !group.GroupMembers.Any(member =>
                        member.UserId == group.HostUserId && member.Status == GroupMemberStatus.Active))
                    {
                        logger.LogError("Rename found inconsistent Host state for group {GroupId}.", group.Id);
                        return Result.Failure<RenameTravelGroupResponse>(
                            TravelGroupErrorCodes.MemberListInconsistent, "Travel group membership is inconsistent.");
                    }

                    var beforeName = group.Name;
                    group.Rename(request.Name!);
                    if (!string.Equals(beforeName, group.Name, StringComparison.Ordinal))
                    {
                        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                            request.HostUserId, AuditActionTypes.TravelGroupRenamed,
                            AuditEntityTypes.TravelGroup, group.Id, clock.UtcNow, AuditOutcome.Success,
                            beforeData: JsonSerializer.Serialize(new { name = beforeName }),
                            afterData: JsonSerializer.Serialize(new { name = group.Name })));
                        await dbContext.SaveChangesAsync(token);
                    }

                    return Result.Success(new RenameTravelGroupResponse(group.Id, group.Name));
                }, cancellationToken);
            }
            catch (Exception exception) when (
                exception is DbUpdateConcurrencyException || dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
                if (attempt < 3)
                {
                    logger.LogWarning(exception, "Retrying rename for group {GroupId}; attempt {Attempt}.",
                        request.GroupId, attempt);
                    continue;
                }

                logger.LogError(exception, "Rename exhausted concurrency retries for group {GroupId}.", request.GroupId);
                return Result.Failure<RenameTravelGroupResponse>(TravelGroupErrorCodes.ConcurrentRetryExhausted,
                    "The group is temporarily busy. Please retry this request.");
            }
        }
    }
}