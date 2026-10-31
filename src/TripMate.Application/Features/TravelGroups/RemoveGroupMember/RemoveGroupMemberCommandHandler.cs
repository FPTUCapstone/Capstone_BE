using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.RemoveGroupMember;

public sealed class RemoveGroupMemberCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    IGroupJoinLock groupLock,
    IItineraryAccessService itineraryAccessService,
    ILogger<RemoveGroupMemberCommandHandler> logger)
    : IRequestHandler<RemoveGroupMemberCommand, Result<RemoveGroupMemberResponse>>
{
    private const int MaxAttempts = 3;

    public async Task<Result<RemoveGroupMemberResponse>> Handle(
        RemoveGroupMemberCommand request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteAttemptAsync(request, cancellationToken);
            }
            catch (Exception exception) when (
                exception is DbUpdateConcurrencyException || dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
                if (attempt < MaxAttempts)
                {
                    logger.LogWarning(exception,
                        "UC-20 retrying removal for group {GroupId}; attempt {Attempt}.",
                        request.GroupId, attempt);
                    continue;
                }

                logger.LogError(exception,
                    "UC-20 removal exhausted concurrency retries for group {GroupId}.",
                    request.GroupId);
                return Result.Failure<RemoveGroupMemberResponse>(
                    TravelGroupErrorCodes.ConcurrentRetryExhausted,
                    "The group is temporarily busy. Please retry with the same Idempotency-Key.");
            }
        }
    }

    private Task<Result<RemoveGroupMemberResponse>> ExecuteAttemptAsync(
        RemoveGroupMemberCommand request, CancellationToken cancellationToken) =>
        dbContext.ExecuteInSerializableTransactionAsync(async transactionToken =>
        {
            await groupLock.AcquireOperationLockAsync(
                request.HostUserId, request.IdempotencyKey, transactionToken);
            await groupLock.AcquireGroupLockAsync(request.GroupId, transactionToken);

            var previous = await dbContext.GroupMemberRemovalOperations.FirstOrDefaultAsync(
                operation => operation.HostUserId == request.HostUserId
                    && operation.IdempotencyKey == request.IdempotencyKey,
                transactionToken);
            if (previous is not null)
            {
                return previous.GroupId == request.GroupId && previous.MemberUserId == request.MemberUserId
                    ? Result.Success(ToResponse(previous))
                    : Result.Failure<RemoveGroupMemberResponse>(
                        TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch,
                        "The Idempotency-Key was already used for a different group or member.");
            }

            var group = await dbContext.TravelGroups
                .Include(entity => entity.GroupMembers)
                .FirstOrDefaultAsync(entity => entity.Id == request.GroupId, transactionToken);
            if (group is null)
            {
                return Result.Failure<RemoveGroupMemberResponse>(
                    TravelGroupErrorCodes.GroupNotFound, "The specified travel group does not exist.");
            }
            if (group.Status != TravelGroupStatus.Active)
            {
                return Result.Failure<RemoveGroupMemberResponse>(
                    TravelGroupErrorCodes.GroupClosed, "This travel group is closed.");
            }

            var activeHost = group.GroupMembers.SingleOrDefault(member =>
                member.UserId == group.HostUserId && member.Status == GroupMemberStatus.Active);
            if (activeHost is null || group.ClosedAtUtc is not null)
            {
                logger.LogError("UC-20 found inconsistent Host state for group {GroupId}.", group.Id);
                return Result.Failure<RemoveGroupMemberResponse>(
                    TravelGroupErrorCodes.MemberListInconsistent, "Travel group membership is inconsistent.");
            }
            if (group.HostUserId != request.HostUserId)
            {
                return Result.Failure<RemoveGroupMemberResponse>(
                    TravelGroupErrorCodes.HostPermissionRequired, "Only the active Host can remove a member.");
            }
            if (request.MemberUserId == group.HostUserId)
            {
                return Result.Failure<RemoveGroupMemberResponse>(
                    TravelGroupErrorCodes.MemberNotActive, "The Host cannot be removed.");
            }

            var member = group.GroupMembers.SingleOrDefault(item => item.UserId == request.MemberUserId);
            if (member is null || member.Status != GroupMemberStatus.Active)
            {
                return Result.Failure<RemoveGroupMemberResponse>(
                    TravelGroupErrorCodes.MemberNotActive, "The target is not an active group member.");
            }

            var memberName = await dbContext.Users
                .Where(user => user.Id == member.UserId)
                .Select(user => user.FullName)
                .SingleOrDefaultAsync(transactionToken);
            if (memberName is null)
            {
                logger.LogError("UC-20 member {MemberUserId} has no User row.", member.UserId);
                return Result.Failure<RemoveGroupMemberResponse>(
                    TravelGroupErrorCodes.MemberListInconsistent, "Travel group membership is inconsistent.");
            }

            var now = dateTimeProvider.UtcNow;
            member.Remove(now);
            // Persist the transition before ResolveCurrentAsync checks for independent itinerary access.
            await dbContext.SaveChangesAsync(transactionToken);

            var openSession = await dbContext.TripSessions
                .Include(session => session.Items)
                .Include(session => session.StateHistory)
                .SingleOrDefaultAsync(session => session.TravelerUserId == member.UserId
                    && session.EndedAtUtc == null, transactionToken);
            if (openSession is not null)
            {
                var groupScheduleId = await dbContext.Itineraries
                    .Where(itinerary => itinerary.Id == group.ItineraryId)
                    .Select(itinerary => itinerary.SchedulingRequestId)
                    .SingleOrDefaultAsync(transactionToken);
                var dependsOnGroup = await dbContext.Itineraries.AnyAsync(
                    itinerary => itinerary.Id == openSession.RequestedItineraryId
                        && (itinerary.Id == group.ItineraryId
                            || (groupScheduleId != null
                                && itinerary.SchedulingRequestId == groupScheduleId)),
                    transactionToken);
                if (dependsOnGroup)
                {
                    var remainingAccess = await itineraryAccessService.ResolveCurrentAsync(
                        openSession.RequestedItineraryId,
                        member.UserId,
                        trackCurrent: false,
                        transactionToken);
                    if (remainingAccess.IsFailure)
                    {
                        if (!openSession.RevokeGroupAccess(now))
                        {
                            throw new InvalidOperationException(
                                "An open group-dependent navigation session could not be revoked.");
                        }
                        dbContext.MarkTripSessionProgressForConcurrencyCheck(openSession);
                    }
                }
            }

            dbContext.GroupMemberRemovalOperations.Add(GroupMemberRemovalOperation.Create(
                request.HostUserId, group.Id, member.UserId, memberName,
                request.IdempotencyKey, now));
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                request.HostUserId,
                AuditActionTypes.TravelGroupMemberRemoved,
                AuditEntityTypes.TravelGroup,
                group.Id,
                now,
                AuditOutcome.Success,
                afterData: JsonSerializer.Serialize(new { groupId = group.Id, memberUserId = member.UserId, removedAtUtc = now })));
            dbContext.Notifications.Add(new Notification
            {
                UserId = member.UserId,
                Channel = NotificationChannel.InApp,
                Type = Notification.TravelGroupMemberRemovedType,
                Title = "Removed from travel group",
                Body = $"You were removed from {group.Name}.",
                RelatedEntityType = Notification.TravelGroupRelatedEntityType,
                RelatedEntityId = group.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = now,
            });
            await dbContext.SaveChangesAsync(transactionToken);
            return Result.Success(new RemoveGroupMemberResponse(group.Id, member.UserId, memberName, now));
        }, cancellationToken);

    private static RemoveGroupMemberResponse ToResponse(GroupMemberRemovalOperation operation) =>
        new(operation.GroupId, operation.MemberUserId, operation.MemberName, operation.RemovedAtUtc);
}