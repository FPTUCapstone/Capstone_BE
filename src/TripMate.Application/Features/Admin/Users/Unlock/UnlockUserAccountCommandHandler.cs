using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Users.Common;
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
        if (currentUserService.UserId is not long administratorUserId
            || currentUserService.Role != nameof(UserRole.Administrator))
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
                administratorUserId,
                normalizedReason,
                normalizedKey,
                requestHash,
                transactionCancellationToken),
            cancellationToken);
    }

    private async Task<Result<UnlockUserAccountResponse>> ExecuteInTransactionAsync(
        long targetUserId,
        long administratorUserId,
        string reason,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        // Always acquire locks in this order. Acquiring the target lock first keeps
        // the serializable idempotency lookup from holding a range lock while it
        // waits for another unlock of the same account.
        await userUnlockLock.AcquireTargetUserAsync(targetUserId, cancellationToken);
        await userUnlockLock.AcquireAsync(administratorUserId, idempotencyKey, cancellationToken);

        UserUnlockOperation? existingOperation = await dbContext.UserUnlockOperations
            .SingleOrDefaultAsync(
                operation => operation.AdministratorUserId == administratorUserId
                    && operation.IdempotencyKey == idempotencyKey,
                cancellationToken);
        if (existingOperation is not null)
        {
            if (existingOperation.RequestHash != requestHash)
            {
                return Result.Failure<UnlockUserAccountResponse>(
                    UserAdministrationErrorCodes.IdempotencyKeyPayloadMismatch,
                    "The Idempotency-Key was already used with different request data.");
            }

            return Result.Success(new UnlockUserAccountResponse(
                existingOperation.TargetUserId,
                existingOperation.RestoredStatus,
                existingOperation.UnlockedAtUtc));
        }

        User? target = await dbContext.Users
            .SingleOrDefaultAsync(user => user.Id == targetUserId, cancellationToken);
        if (target is null)
        {
            return Result.Failure<UnlockUserAccountResponse>(
                UserAdministrationErrorCodes.NotFound,
                "The user account was not found.");
        }

        if (target.Role == UserRole.Administrator)
        {
            return Result.Failure<UnlockUserAccountResponse>(
                UserAdministrationErrorCodes.ProtectedAdministrator,
                "Administrator accounts cannot be unlocked through this endpoint.");
        }

        if (target.Status != AccountStatus.Locked)
        {
            return Result.Failure<UnlockUserAccountResponse>(
                UserAdministrationErrorCodes.NotLocked,
                "The user account is not locked.");
        }

        if (!target.TryRestoreFromLock(out AccountStatus restoredStatus))
        {
            return Result.Failure<UnlockUserAccountResponse>(
                UserAdministrationErrorCodes.LockRecoveryStateMissing,
                "The historical lock record is incomplete and cannot be safely restored.");
        }

        var now = dateTimeProvider.UtcNow;
        target.UpdatedAtUtc = now;
        await dbContext.RevokeActiveRefreshTokensForUserAsync(target.Id, now, cancellationToken);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            actorUserId: administratorUserId,
            actionType: AuditActionTypes.UserUnlock,
            affectedEntity: AuditEntityTypes.User,
            affectedEntityId: target.Id,
            createdAtUtc: now,
            result: AuditOutcome.Success,
            reason: reason,
            beforeData: JsonSerializer.Serialize(new { status = AccountStatus.Locked.ToString() }),
            afterData: JsonSerializer.Serialize(new { status = restoredStatus.ToString() })));
        dbContext.UserUnlockOperations.Add(UserUnlockOperation.Create(
            administratorUserId,
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