using System.Globalization;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.UserAccounts;

internal static class AccountLockRecovery
{
    public const string IncompleteMessage = "The historical lock record is incomplete and cannot be safely restored.";

    public static async Task<decimal?> GetOutstandingAmountAsync(
        IApplicationDbContext db, User target, DateTimeOffset now, CancellationToken ct)
    {
        if (target.Role == UserRole.TourOperator)
        {
            var debts = db.TourOperatorDebts
                .Where(debt => debt.TourOperatorUserId == target.Id && debt.RecoveredAmount < debt.Amount);
            // A separate manual lock can be resolved during the debt grace period.
            if (target.LockedByUserId is not null)
            {
                var cutoff = now - DailyPayoutAutoLock.UnrecoveredDebtGracePeriod;
                if (!await debts.AnyAsync(debt => debt.IncurredAtUtc <= cutoff, ct))
                    return null;
            }
            var amount = await debts.SumAsync(debt => debt.Amount - debt.RecoveredAmount, ct);
            return amount > 0 ? amount : null;
        }

        if (target.Role == UserRole.ServiceProvider)
        {
            var today = DateOnly.FromDateTime(now.ToOffset(ServiceBooking.VietnamOffset).DateTime);
            var overdue = db.ServiceCommissionStatements
                .Where(statement => statement.Provider.UserId == target.Id && statement.CommissionAmount > 0
                    && statement.DueDate < today && statement.Status != ServiceCommissionStatement.StatusPaid);
            if (target.LockedByUserId is not null)
            {
                var cutoff = today.AddDays(-ServiceCommissionStatement.AutoLockGraceDays);
                if (!await overdue.AnyAsync(statement => statement.DueDate <= cutoff, ct))
                    return null;
            }
            return await overdue.GroupBy(_ => 1)
                .Select(statements => (decimal?)statements.Sum(statement => statement.CommissionAmount))
                .SingleOrDefaultAsync(ct);
        }

        return null;
    }

    public static Result<T> PaymentRequired<T>(string errorCode, decimal amount)
    {
        var formattedAmount = amount.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
        return Result.Failure<T>(
            errorCode,
            $"This account cannot be unlocked until its outstanding amount of {formattedAmount} VND is fully paid.",
            new Dictionary<string, object?> { ["messageCode"] = "MSG199", ["outstandingAmountVnd"] = amount });
    }

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

        // Historical provider auto-lock records may lack an explicit outcome.
        var recorded = await db.AuditLogs.AnyAsync(audit => audit.ActionType == action && audit.ActorUserId == null
            && audit.AffectedEntity == AuditEntityTypes.User && audit.AffectedEntityId == target.Id
            && audit.CreatedAtUtc == target.LockedAtUtc && audit.Result != AuditOutcome.Failure, ct);
        if (!recorded) return false;

        target.Unlock(now);
        return true;
    }
}
