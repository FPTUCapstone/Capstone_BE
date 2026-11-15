using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.LeaveTravelGroup;

public sealed class LeaveTravelGroupCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    IGroupLeaveLock leaveLock,
    IItineraryAccessService itineraryAccessService,
    ILogger<LeaveTravelGroupCommandHandler> logger)
    : IRequestHandler<LeaveTravelGroupCommand, Result<LeaveTravelGroupResponse>>
{
    private const int MaxAttempts = 3;

    public async Task<Result<LeaveTravelGroupResponse>> Handle(
        LeaveTravelGroupCommand request,
        CancellationToken cancellationToken)
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
                    logger.LogWarning(
                        exception,
                        "UC-21 retrying leave after a concurrency conflict for group {GroupId}; attempt {Attempt}.",
                        request.GroupId,
                        attempt);
                    continue;
                }

                logger.LogError(
                    exception,
                    "UC-21 leave exhausted concurrency retries for group {GroupId}.",
                    request.GroupId);
                return Result.Failure<LeaveTravelGroupResponse>(
                    TravelGroupErrorCodes.ConcurrentRetryExhausted,
                    "The group is temporarily busy. Please retry this request with the same Idempotency-Key.");
            }
        }
    }

    private Task<Result<LeaveTravelGroupResponse>> ExecuteAttemptAsync(
        LeaveTravelGroupCommand request,
        CancellationToken cancellationToken) =>
        dbContext.ExecuteInSerializableTransactionAsync(
            async transactionToken =>
            {
                await leaveLock.AcquireOperationLockAsync(
                    request.TravelerUserId,
                    request.IdempotencyKey,
                    transactionToken);
                await leaveLock.AcquireGroupLockAsync(request.GroupId, transactionToken);

                var previous = await dbContext.GroupLeaveOperations.FirstOrDefaultAsync(
                    operation => operation.TravelerUserId == request.TravelerUserId
                        && operation.IdempotencyKey == request.IdempotencyKey,
                    transactionToken);
                if (previous is not null)
                {
                    return previous.GroupId == request.GroupId
                        ? Result.Success(ToResponse(previous))
                        : Result.Failure<LeaveTravelGroupResponse>(
                            TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch,
                            "The Idempotency-Key was already used for another travel group.");
                }

                // The aggregate's Close/TransferHostTo guards need every member loaded.
                var group = await dbContext.TravelGroups
                    .Include(entity => entity.GroupMembers)
                    .Include(entity => entity.GroupInvitations)
                    .FirstOrDefaultAsync(entity => entity.Id == request.GroupId, transactionToken);
                if (group is null)
                {
                    return Result.Failure<LeaveTravelGroupResponse>(
                        TravelGroupErrorCodes.GroupNotFound,
                        "The specified travel group does not exist.");
                }

                if (group.Status == TravelGroupStatus.Closed)
                {
                    return Result.Failure<LeaveTravelGroupResponse>(
                        TravelGroupErrorCodes.GroupClosed,
                        "This travel group is closed.");
                }

                var member = group.GroupMembers.SingleOrDefault(item => item.UserId == request.TravelerUserId);
                if (member is null || member.Status == GroupMemberStatus.Removed)
                {
                    return Result.Failure<LeaveTravelGroupResponse>(
                        TravelGroupErrorCodes.ActiveMembershipRequired,
                        "Active group membership is required to leave.");
                }

                if (member.Status == GroupMemberStatus.Left)
                {
                    return Result.Failure<LeaveTravelGroupResponse>(
                        TravelGroupErrorCodes.AlreadyLeft,
                        "You have already left this travel group.");
                }

                var activeHost = group.GroupMembers.SingleOrDefault(item =>
                    item.UserId == group.HostUserId && item.Status == GroupMemberStatus.Active);
                if (activeHost is null || group.ClosedAtUtc is not null)
                {
                    logger.LogError(
                        "UC-21 refused leave for group {GroupId}: persisted Host/member lifecycle is inconsistent.",
                        group.Id);
                    return Result.Failure<LeaveTravelGroupResponse>(
                        TravelGroupErrorCodes.MemberListInconsistent,
                        "Travel group membership is inconsistent.");
                }

                var now = dateTimeProvider.UtcNow;
                member.Leave(now);
                long? successorUserId = null;
                bool closed = false;
                if (member.UserId == group.HostUserId)
                {
                    var successor = group.GroupMembers
                        .Where(item => item.Status == GroupMemberStatus.Active)
                        .OrderBy(item => item.JoinedAtUtc)
                        .ThenBy(item => item.UserId)
                        .FirstOrDefault();
                    if (successor is not null)
                    {
                        group.TransferHostTo(successor);
                        successorUserId = successor.UserId;
                        dbContext.Notifications.Add(new Notification
                        {
                            UserId = successor.UserId,
                            Channel = NotificationChannel.InApp,
                            Type = Notification.TravelGroupHostTransferredType,
                            Title = "You are now the group Host",
                            Body = "You can manage this travel group's members and invitations.",
                            RelatedEntityType = Notification.TravelGroupRelatedEntityType,
                            RelatedEntityId = group.Id,
                            Status = NotificationStatus.Pending,
                            CreatedAtUtc = now,
                        });
                    }
                    else
                    {
                        group.Close(now);
                        closed = true;
                        foreach (var invitation in group.GroupInvitations.Where(item => item.IsUsableAt(now)))
                        {
                            invitation.Expire(now);
                        }
                    }
                }

                // Persist the membership transition inside this transaction before checking
                // whether an open navigation session still has an independent access path.
                await dbContext.SaveChangesAsync(transactionToken);
                var openSession = await dbContext.TripSessions
                    .Include(session => session.Items)
                    .Include(session => session.StateHistory)
                    .SingleOrDefaultAsync(session => session.TravelerUserId == request.TravelerUserId
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
                            request.TravelerUserId,
                            trackCurrent: false,
                            transactionToken);
                        if (remainingAccess.IsFailure && openSession.RevokeGroupAccess(now))
                        {
                            dbContext.MarkTripSessionProgressForConcurrencyCheck(openSession);
                        }
                    }
                }

                var operation = GroupLeaveOperation.Create(
                    request.TravelerUserId,
                    group.Id,
                    request.IdempotencyKey,
                    closed,
                    successorUserId,
                    now);
                dbContext.GroupLeaveOperations.Add(operation);
                await dbContext.SaveChangesAsync(transactionToken);
                return Result.Success(ToResponse(operation));
            },
            cancellationToken);

    private static LeaveTravelGroupResponse ToResponse(GroupLeaveOperation operation) =>
        new(
            operation.GroupId,
            nameof(GroupMemberStatus.Left),
            operation.GroupClosed,
            operation.NewHostUserId,
            operation.LeftAtUtc);
}