using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TravelGroups.Common;
using TripMate.Application.Features.TravelGroups.GetInvitation;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TravelGroups.ManageInvitation;

public sealed class GetOrCreateGroupInvitationCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    IGroupInvitationLock invitationLock,
    IGroupInvitationCodeGenerator codeGenerator)
    : IRequestHandler<GetOrCreateGroupInvitationCommand, Result<GetGroupInvitationResponse>>
{
    public Task<Result<GetGroupInvitationResponse>> Handle(
        GetOrCreateGroupInvitationCommand request,
        CancellationToken cancellationToken) =>
        GroupInvitationCommandExecutor.ExecuteAsync(
            dbContext,
            dateTimeProvider,
            invitationLock,
            codeGenerator,
            request.GroupId,
            request.CurrentUserId,
            request.IdempotencyKey,
            GroupInvitationOperationType.GetOrCreate,
            cancellationToken);
}

public sealed class RegenerateGroupInvitationCommandHandler(
    IApplicationDbContext dbContext,
    IDateTimeProvider dateTimeProvider,
    IGroupInvitationLock invitationLock,
    IGroupInvitationCodeGenerator codeGenerator)
    : IRequestHandler<RegenerateGroupInvitationCommand, Result<GetGroupInvitationResponse>>
{
    public Task<Result<GetGroupInvitationResponse>> Handle(
        RegenerateGroupInvitationCommand request,
        CancellationToken cancellationToken) =>
        GroupInvitationCommandExecutor.ExecuteAsync(
            dbContext,
            dateTimeProvider,
            invitationLock,
            codeGenerator,
            request.GroupId,
            request.CurrentUserId,
            request.IdempotencyKey,
            GroupInvitationOperationType.Regenerate,
            cancellationToken);
}

internal static class GroupInvitationCommandExecutor
{
    private const int MaximumCodeGenerationAttempts = 5;

    public static Task<Result<GetGroupInvitationResponse>> ExecuteAsync(
        IApplicationDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        IGroupInvitationLock invitationLock,
        IGroupInvitationCodeGenerator codeGenerator,
        long groupId,
        long currentUserId,
        Guid idempotencyKey,
        GroupInvitationOperationType operationType,
        CancellationToken cancellationToken) =>
        dbContext.ExecuteInSerializableTransactionAsync(
            transactionCancellationToken => ExecuteInTransactionAsync(
                dbContext,
                dateTimeProvider,
                invitationLock,
                codeGenerator,
                groupId,
                currentUserId,
                idempotencyKey,
                operationType,
                transactionCancellationToken),
            cancellationToken);

    private static async Task<Result<GetGroupInvitationResponse>> ExecuteInTransactionAsync(
        IApplicationDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        IGroupInvitationLock invitationLock,
        IGroupInvitationCodeGenerator codeGenerator,
        long groupId,
        long currentUserId,
        Guid idempotencyKey,
        GroupInvitationOperationType operationType,
        CancellationToken cancellationToken)
    {
        await invitationLock.AcquireAsync(groupId, currentUserId, idempotencyKey, cancellationToken);

        var previousOperation = await dbContext.GroupInvitationOperations
            .Include(operation => operation.Invitation)
            .FirstOrDefaultAsync(
                operation => operation.TravelerUserId == currentUserId
                    && operation.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (previousOperation is not null)
        {
            if (previousOperation.GroupId != groupId || previousOperation.OperationType != operationType)
            {
                return Result.Failure<GetGroupInvitationResponse>(
                    TravelGroupErrorCodes.IdempotencyKeyPayloadMismatch,
                    "The Idempotency-Key was already used with different request data.");
            }

            var replayGroup = await dbContext.TravelGroups.FirstOrDefaultAsync(
                group => group.Id == groupId,
                cancellationToken);
            return replayGroup is null
                ? Result.Failure<GetGroupInvitationResponse>(
                    TravelGroupErrorCodes.GroupNotFound,
                    "The specified travel group does not exist.")
                : Result.Success(ToResponse(replayGroup, previousOperation.Invitation));
        }

        var group = await dbContext.TravelGroups.FirstOrDefaultAsync(
            entity => entity.Id == groupId,
            cancellationToken);
        if (group is null)
        {
            return Result.Failure<GetGroupInvitationResponse>(
                TravelGroupErrorCodes.GroupNotFound,
                "The specified travel group does not exist.");
        }

        if (group.HostUserId != currentUserId)
        {
            return Result.Failure<GetGroupInvitationResponse>(
                TravelGroupErrorCodes.HostPermissionRequired,
                "Only the Group Host can manage invitations.");
        }

        var now = dateTimeProvider.UtcNow;
        GroupInvitation invitation;
        if (operationType == GroupInvitationOperationType.GetOrCreate)
        {
            invitation = await dbContext.GroupInvitations
                .Where(item => item.GroupId == group.Id
                    && item.ExpiresAtUtc > now
                    && item.UsedCount < item.MaxUses)
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                ?? await CreateUniqueInvitationAsync(
                    dbContext,
                    codeGenerator,
                    group.Id,
                    currentUserId,
                    now,
                    cancellationToken);
        }
        else
        {
            var usableInvitations = await dbContext.GroupInvitations
                .Where(item => item.GroupId == group.Id
                    && item.ExpiresAtUtc > now
                    && item.UsedCount < item.MaxUses)
                .ToListAsync(cancellationToken);
            foreach (var usableInvitation in usableInvitations)
            {
                usableInvitation.Expire(now);
            }

            invitation = await CreateUniqueInvitationAsync(
                dbContext,
                codeGenerator,
                group.Id,
                currentUserId,
                now,
                cancellationToken);
        }

        var operation = GroupInvitationOperation.Create(
            currentUserId,
            group.Id,
            operationType,
            idempotencyKey,
            invitation,
            now);
        dbContext.GroupInvitationOperations.Add(operation);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(ToResponse(group, invitation));
    }

    private static async Task<GroupInvitation> CreateUniqueInvitationAsync(
        IApplicationDbContext dbContext,
        IGroupInvitationCodeGenerator codeGenerator,
        long groupId,
        long currentUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumCodeGenerationAttempts; attempt++)
        {
            var inviteCode = codeGenerator.Generate();
            var exists = await dbContext.GroupInvitations.AnyAsync(
                invitation => invitation.InviteCode == inviteCode,
                cancellationToken);
            if (exists)
            {
                continue;
            }

            var invitation = GroupInvitation.Create(
                groupId,
                currentUserId,
                inviteCode,
                now.AddDays(TravelGroupConstants.InvitationCodeExpiryDays),
                TravelGroupConstants.UnlimitedInvitationUses,
                now);
            dbContext.GroupInvitations.Add(invitation);
            return invitation;
        }

        throw new InvalidOperationException("Could not generate a unique invitation code.");
    }

    private static GetGroupInvitationResponse ToResponse(TravelGroup group, GroupInvitation invitation) =>
        new(
            group.Id,
            group.Name,
            invitation.InviteCode,
            $"tripmate://groups/join?code={invitation.InviteCode}",
            invitation.ExpiresAtUtc);
}