using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.JoinTravelGroup;

public sealed class JoinTravelGroupCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    IGroupJoinLock joinLock)
    : IRequestHandler<JoinTravelGroupCommand, Result<JoinTravelGroupResponse>>
{
    public async Task<Result<JoinTravelGroupResponse>> Handle(
        JoinTravelGroupCommand request,
        CancellationToken cancellationToken)
    {
        var normalizedCode = request.InvitationCode.Trim().ToUpperInvariant();

        var targetGroupId = await ResolveGroupIdAsync(
            request.TravelerUserId,
            request.IdempotencyKey,
            normalizedCode,
            cancellationToken);

        if (!targetGroupId.HasValue)
        {
            return Result.Failure<JoinTravelGroupResponse>(
                TravelGroupErrorCodes.InvitationUnavailable,
                "The invitation code is invalid, expired, or has reached its maximum uses.");
        }

        return await dbContext.ExecuteInSerializableTransactionAsync(
            transactionCancellationToken => ExecuteInTransactionAsync(
                normalizedCode,
                request.TravelerUserId,
                request.IdempotencyKey,
                targetGroupId.Value,
                transactionCancellationToken),
            cancellationToken);
    }

    private async Task<long?> ResolveGroupIdAsync(
        long travelerUserId,
        Guid idempotencyKey,
        string normalizedCode,
        CancellationToken cancellationToken)
    {
        var existingOpGroupId = await dbContext.GroupJoinOperations
            .AsNoTracking()
            .Where(op => op.TravelerUserId == travelerUserId && op.IdempotencyKey == idempotencyKey)
            .Select(op => (long?)op.GroupId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingOpGroupId.HasValue)
        {
            return existingOpGroupId.Value;
        }

        return await dbContext.GroupInvitations
            .AsNoTracking()
            .Where(inv => inv.InviteCode == normalizedCode)
            .Select(inv => (long?)inv.GroupId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Result<JoinTravelGroupResponse>> ExecuteInTransactionAsync(
        string normalizedCode,
        long travelerUserId,
        Guid idempotencyKey,
        long targetGroupId,
        CancellationToken cancellationToken)
    {
        var now = dateTimeProvider.UtcNow;

        // 1. Acquire operation lock: Traveler + UUID
        await joinLock.AcquireOperationLockAsync(travelerUserId, idempotencyKey, cancellationToken);

        // 2. Acquire code lock: normalized invitation code
        await joinLock.AcquireCodeLockAsync(normalizedCode, cancellationToken);

        // 3. Acquire group lock before querying tables in serializable transaction
        await joinLock.AcquireGroupLockAsync(targetGroupId, cancellationToken);

        // Check for existing operation replay (e.g. retry after success, even if code expired or regenerated)
        var existingOperation = await dbContext.GroupJoinOperations
            .Include(op => op.TravelGroup)
            .FirstOrDefaultAsync(
                op => op.TravelerUserId == travelerUserId && op.IdempotencyKey == idempotencyKey,
                cancellationToken);

        if (existingOperation is not null)
        {
            if (existingOperation.InvitationCode != normalizedCode)
            {
                return Result.Failure<JoinTravelGroupResponse>(
                    TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch,
                    "The Idempotency-Key was already used with different request data.");
            }

            if (existingOperation.TravelGroup is null)
            {
                return Result.Failure<JoinTravelGroupResponse>(
                    TravelGroupErrorCodes.GroupNotFound,
                    "The travel group associated with this operation could not be found.");
            }

            return Result.Success(
                new JoinTravelGroupResponse(
                    existingOperation.GroupId,
                    existingOperation.TravelGroup.Name,
                    existingOperation.TravelGroup.ItineraryId));
        }

        // 4. Read invitation
        var invitation = await dbContext.GroupInvitations
            .Include(inv => inv.TravelGroup)
            .FirstOrDefaultAsync(
                inv => inv.InviteCode == normalizedCode,
                cancellationToken);

        if (invitation is null || !invitation.IsUsableAt(now) || invitation.TravelGroup is null || invitation.GroupId != targetGroupId)
        {
            return Result.Failure<JoinTravelGroupResponse>(
                TravelGroupErrorCodes.InvitationUnavailable,
                "The invitation code is invalid, expired, or has reached its maximum uses.");
        }

        var travelGroup = invitation.TravelGroup;
        var groupId = travelGroup.Id;

        // 5. Check existing membership
        var existingMember = await dbContext.GroupMembers
            .FirstOrDefaultAsync(
                m => m.GroupId == groupId && m.UserId == travelerUserId,
                cancellationToken);

        if (existingMember is not null)
        {
            if (existingMember.Status == GroupMemberStatus.Active)
            {
                return Result.Failure<JoinTravelGroupResponse>(
                    TravelGroupErrorCodes.AlreadyActiveMember,
                    "You are already an active member of this travel group.",
                    new Dictionary<string, object?> { ["groupId"] = groupId });
            }

            if (existingMember.Status == GroupMemberStatus.Left
                || existingMember.Status == GroupMemberStatus.Removed)
            {
                existingMember.Reactivate(now);
            }
        }
        else
        {
            var newMember = GroupMember.CreateMember(travelGroup, travelerUserId, now);
            dbContext.GroupMembers.Add(newMember);
        }

        // Increment invitation usage
        invitation.IncrementUsedCount();

        // Record operation for idempotency
        var operation = GroupJoinOperation.Create(
            travelerUserId,
            groupId,
            invitation.Id,
            normalizedCode,
            idempotencyKey,
            now);

        dbContext.GroupJoinOperations.Add(operation);

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(
            new JoinTravelGroupResponse(
                groupId,
                travelGroup.Name,
                travelGroup.ItineraryId));
    }
}