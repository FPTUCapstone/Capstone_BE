using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.UserAccounts;

internal static class AccountLockRecovery
{
    public const string IncompleteMessage = "The historical lock record is incomplete and cannot be safely restored.";

    public static async Task<bool> TryRestoreAsync(IApplicationDbContext db, User target, DateTimeOffset now, CancellationToken ct)
    {
        if (target.TryRestoreFromLock(out _))
        {
            target.UpdatedAtUtc = now;
            return true;
        }

        var action = target.Role switch
        {
            UserRole.ServiceProvider => AuditActionTypes.ServiceProviderAutoLocked,
            UserRole.TourOperator => AuditActionTypes.TourOperatorAutoLocked,
            _ => null,
        };
        if (action is null || target.Status != AccountStatus.Locked || target.LockedByUserId is not null
            || target.LockedAtUtc is null || target.StatusBeforeLock is not { } previous || previous == AccountStatus.Locked
            || !Enum.IsDefined(previous) || string.IsNullOrWhiteSpace(target.LockReason))
        {
            return false;
        }

        // The Provider auto-lock job writes audit rows without an explicit outcome.
        var recorded = await db.AuditLogs.AnyAsync(audit => audit.ActionType == action && audit.ActorUserId == null
            && audit.AffectedEntity == AuditEntityTypes.User && audit.AffectedEntityId == target.Id
            && audit.CreatedAtUtc == target.LockedAtUtc && audit.Result != AuditOutcome.Failure, ct);
        if (!recorded) return false;

        target.Unlock(now);
        return true;
    }
}