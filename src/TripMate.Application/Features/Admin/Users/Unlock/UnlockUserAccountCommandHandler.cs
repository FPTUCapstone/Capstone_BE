using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.UserAccounts;
using TripMate.Application.Features.Admin.Users.Common;
using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Users.Unlock;

public sealed class UnlockUserAccountCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider,
    IUserUnlockLock userUnlockLock)
    : IRequestHandler<UnlockUserAccountCommand, Result<UnlockUserAccountResponse>>
{
    public async Task<Result<UnlockUserAccountResponse>> Handle(
        UnlockUserAccountCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not > 0)
        {
            return Result.Failure<UnlockUserAccountResponse>(
                UserAdministrationErrorCodes.Forbidden,
                "You do not have permission to unlock user accounts.");
        }

        var normalizedReason = request.Reason.Trim();
        var normalizedKey = request.IdempotencyKey.Trim();
        var requestHash = ComputeRequestHash(request.UserId, normalizedReason);

        return await dbContext.ExecuteInSerializableTransactionAsync(
            transactionCancellationToken => ExecuteInTransactionAsync(
                request.UserId,
                currentUserService.UserId.Value,
                normalizedReason,
                normalizedKey,
                requestHash,
                transactionCancellationToken),
            cancellationToken);
    }

    private async Task<Result<UnlockUserAccountResponse>> ExecuteInTransactionAsync(
        long targetUserId,
        long actorUserId,
        string reason,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        // Always acquire locks in this order. Acquiring the target lock first keeps
        // the serializable idempotency lookup from holding a range lock while it
        // waits for another unlock of the same account.
        await userUnlockLock.AcquireTargetUserAsync(targetUserId, cancellationToken);
        await userUnlockLock.AcquireAsync(actorUserId, idempotencyKey, cancellationToken);

        var role = await UserAccountAccess.ActorRoleAsync(dbContext, currentUserService, cancellationToken);
        if (role is null)
        {
            return Result.Failure<UnlockUserAccountResponse>(
                UserAdministrationErrorCodes.Forbidden,
                "You do not have permission to unlock user accounts.");
        }

        User? target = await UserAccountAccess.Scope(dbContext, role.Value)
            .SingleOrDefaultAsync(user => user.Id == targetUserId, cancellationToken);
        if (target is null)
        {
            return Result.Failure<UnlockUserAccountResponse>(
                UserAdministrationErrorCodes.NotFound,
                "The user account was not found.");
        }

        UserUnlockOperation? existingOperation = await dbContext.UserUnlockOperations
            .SingleOrDefaultAsync(
                operation => operation.AdministratorUserId == actorUserId
                    && operation.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingOperation is not null)
        {
            return existingOperation.RequestHash != requestHash
                ? Result.Failure<UnlockUserAccountResponse>(
                    UserAdministrationErrorCodes.IdempotencyKeyPayloadMismatch,
                    "The Idempotency-Key was already used with different request data.")
                : Result.Success(new UnlockUserAccountResponse(
                existingOperation.TargetUserId,
                existingOperation.RestoredStatus,
                existingOperation.UnlockedAtUtc));
        }

        if (target.Status != AccountStatus.Locked)
        {
            return Result.Failure<UnlockUserAccountResponse>(
                UserAdministrationErrorCodes.NotLocked,
                "The user account is not locked.");
        }

        if (target.Role == UserRole.TourOperator
            && await dbContext.TourOperatorDebts.AnyAsync(
                debt => debt.TourOperatorUserId == target.Id && debt.RecoveredAmount < debt.Amount,
                cancellationToken))
        {
            return Result.Failure<UnlockUserAccountResponse>(
                DailyPayoutErrorCodes.OperatorHasOutstandingDebt,
                DailyPayoutErrorCodes.OperatorHasOutstandingDebtMessage);
        }

        var now = dateTimeProvider.UtcNow;
        if (target.Role == UserRole.ServiceProvider
            && await UserAccountAccess.HasUnpaidOverdueCommissionAsync(dbContext, target.Id, now, cancellationToken))
        {
            return Result.Failure<UnlockUserAccountResponse>(UserAccountErrors.ProviderSettlementUnavailable,
                "Provider commission settlement must be verified before unlocking.");
        }

        // Capture the lock evidence before restoration clears it. An unlock audit must explain
        // both why/when the account was locked and exactly which status was restored.
        var previousLock = new
        {
            previousStatus = target.StatusBeforeLock?.ToString(),
            previousLockedByUserId = target.LockedByUserId,
            previousLockedAtUtc = target.LockedAtUtc,
            previousLockReason = target.LockReason,
        };

        if (!await AccountLockRecovery.TryRestoreAsync(dbContext, target, now, cancellationToken))
        {
            return Result.Failure<UnlockUserAccountResponse>(UserAdministrationErrorCodes.LockRecoveryStateMissing,
                AccountLockRecovery.IncompleteMessage);
        }

        var restoredStatus = target.Status;
        await dbContext.RevokeAllRefreshTokensForUserAsync(target.Id, now, cancellationToken);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            actorUserId: actorUserId,
            actionType: AuditActionTypes.UserUnlock,
            affectedEntity: AuditEntityTypes.User,
            affectedEntityId: target.Id,
            createdAtUtc: now,
            result: AuditOutcome.Success,
            reason: reason,
            beforeData: JsonSerializer.Serialize(previousLock),
            afterData: JsonSerializer.Serialize(new
            {
                status = restoredStatus.ToString(),
                lockedByUserId = target.LockedByUserId,
                lockedAtUtc = target.LockedAtUtc,
                lockReason = target.LockReason,
            })));
        dbContext.Notifications.Add(new Notification
        {
            User = target,
            Channel = NotificationChannel.Email,
            Type = UserAdministrationNotificationTypes.AccountUnlocked,
            Title = UserAdministrationNotificationTypes.AccountUnlockedTitle,
            Body = UserAdministrationNotificationTypes.AccountUnlockedBody,
            RelatedEntityType = AuditEntityTypes.User,
            RelatedEntityId = target.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now,
        });
        dbContext.UserUnlockOperations.Add(UserUnlockOperation.Create(
            actorUserId,
            idempotencyKey,
            requestHash,
            target.Id,
            restoredStatus,
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new UnlockUserAccountResponse(target.Id, restoredStatus, now));
    }

    private static string ComputeRequestHash(long targetUserId, string normalizedReason)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"{targetUserId}\n{normalizedReason}");
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }
}