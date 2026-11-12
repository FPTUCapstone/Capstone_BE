using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement;

public enum DailyPayoutRunOutcome
{
    /// <summary>Vietnam-local time has not reached 10:00 yet; nothing to do.</summary>
    NotDueYet,

    /// <summary>A <see cref="PayoutRun"/> already exists for today's Vietnam-local date — this
    /// run already happened. There is no same-day catch-up in v6 (PD-16 removed); a booking
    /// missed by today's run is simply picked up by tomorrow's.</summary>
    AlreadyRan,

    /// <summary>The run was performed just now.</summary>
    Ran,
}

/// <summary>
/// DAILY-PAYOUT-TRANSFER spec v7 (SRS rev10-6 UC-71, UC-72, UC-74; BR-141, BR-142, BR-144,
/// BR-146, BR-160, BR-177, BR-188, BR-189, PD-9A, PD-11, PD-14, PD-15). Intended to be invoked by
/// a background service polling at a short interval — <see cref="RunIfDueAsync"/> itself decides
/// whether it is time, and whether today's run already happened, so polling more often than once
/// a day is always safe (idempotent, PD-11).
/// </summary>
public sealed class DailyPayoutRunProcessor(
    IApplicationDbContext db,
    DailyPayoutEligibilityReader reader,
    DailyPayoutRetrySweepProcessor retryProcessor,
    IDateTimeProvider clock)
{
    public async Task<DailyPayoutRunOutcome> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        if (!DailyPayoutClock.IsAtOrAfterRunStart(nowUtc))
            return DailyPayoutRunOutcome.NotDueYet;

        var runDate = DailyPayoutClock.VietnamDateOf(nowUtc);
        var alreadyExists = await db.PayoutRuns.AsNoTracking().AnyAsync(r => r.RunDate == runDate, cancellationToken);
        if (alreadyExists)
            return DailyPayoutRunOutcome.AlreadyRan;

        await db.ExecuteInTransactionAsync(async ct =>
        {
            var run = PayoutRun.Start(runDate, nowUtc);
            db.PayoutRuns.Add(run);
            await db.SaveChangesAsync(ct);

            await reader.LogCancelledExclusionsAsync(nowUtc, ct); // PD-2A

            var batches = await CreateBatchesAsync(runDate, nowUtc, ct);
            await DispatchAsync(batches, nowUtc, ct);
            await ApplyAutomaticLocksAsync(nowUtc, ct); // BR-189, PD-9A

            run.Complete(clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        return DailyPayoutRunOutcome.Ran;
    }

    /// <summary>Creates one <see cref="PayoutBatch"/> per Tour Operator that has at least one
    /// eligible Advance or Final settlement this run (PD-2), and one <see cref="PayoutSettlement"/>
    /// per such booking (PD-5). Scheduling does not check the operator's lock status (BR-160) —
    /// a Locked operator's bookings are still scheduled on time; only the transfer, in
    /// <see cref="DispatchAsync"/>, is deferred while locked.</summary>
    private async Task<List<PayoutBatch>> CreateBatchesAsync(DateOnly runDate, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var eligible = await reader.LoadEligibleBookingsAsync(nowUtc, ct);
        var debtByOperator = await reader.LoadOutstandingDebtByOperatorAsync(ct);
        var batchesByOperator = new Dictionary<long, PayoutBatch>();

        foreach (var group in eligible.GroupBy(e => e.TourOperatorUserId))
        {
            var operatorId = group.Key;
            var ordered = group.OrderBy(e => e.OrderKey).ToList();
            var debt = debtByOperator.TryGetValue(operatorId, out var d) ? d : 0m;
            var inputs = ordered
                .Select(e => new DailySettlementInput(e.BookingId, e.Kind, e.PaidAmount, e.RefundAmount, e.CommissionRatePercent))
                .ToList();
            var calculation = DailyPayoutAmountCalculator.Calculate(inputs, debt);

            var batch = PayoutBatch.Create(runDate, operatorId, nowUtc);
            db.PayoutBatches.Add(batch);
            await db.SaveChangesAsync(ct); // assigns batch.Id, needed by the settlements below

            foreach (var (booking, result) in ordered.Zip(calculation.Settlements))
            {
                var settlement = PayoutSettlement.Create(
                    batch.Id, booking.BookingId, operatorId, runDate, result.Kind,
                    result.GrossAmount, result.CommissionRate, result.CommissionAmount,
                    result.NetAmount, result.DebtOffsetAmount, result.PayoutAmount, nowUtc);
                db.PayoutSettlements.Add(settlement);
                batch.AddSettlement(settlement);
            }

            batchesByOperator[operatorId] = batch;
        }

        await db.SaveChangesAsync(ct);

        return batchesByOperator.Values.ToList();
    }

    /// <summary>BR-189, PD-9A: locks every Tour Operator whose oldest unrecovered debt has been
    /// outstanding for at least 15 days, revokes every active session (BR-175), and notifies the
    /// operator and Staff (MSG198).</summary>
    private async Task ApplyAutomaticLocksAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var overdueOperatorIds = await reader.LoadOperatorsOverdueForAutoLockAsync(nowUtc, ct);
        if (overdueOperatorIds.Count == 0)
            return;

        var operators = await db.Users
            .Where(u => overdueOperatorIds.Contains(u.Id))
            .ToListAsync(ct);

        var staffIds = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Staff && u.Status == AccountStatus.Active)
            .Select(u => u.Id)
            .ToListAsync(ct);

        foreach (var operatorUser in operators)
        {
            operatorUser.Lock(DailyPayoutAutoLock.UnrecoveredDebtLockReason, nowUtc);
            await db.RevokeAllRefreshTokensForUserAsync(operatorUser.Id, nowUtc, ct);

            db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                actorUserId: null,
                AuditActionTypes.TourOperatorAutoLocked,
                AuditEntityTypes.User,
                operatorUser.Id,
                nowUtc,
                AuditOutcome.Success,
                reason: DailyPayoutAutoLock.UnrecoveredDebtLockReason));

            NotifyLocked(operatorUser.Id, nowUtc); // MSG198 to the operator
            foreach (var staffId in staffIds)
                NotifyLocked(staffId, nowUtc); // MSG198 to Staff
        }

        await db.SaveChangesAsync(ct);
    }

    private void NotifyLocked(long userId, DateTimeOffset nowUtc) =>
        db.Notifications.Add(new Notification
        {
            UserId = userId,
            Channel = NotificationChannel.Push,
            Type = "TourOperatorAutoLocked",
            Title = "Tour Operator account locked",
            Body = $"{DailyPayoutAutoLock.UnrecoveredDebtLockReason}.",
            RelatedEntityType = AuditEntityTypes.User,
            RelatedEntityId = userId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc,
        });

    /// <summary>Sends the first transfer attempt for every batch just created (PD-6, PD-7),
    /// through the same <see cref="DailyPayoutRetrySweepProcessor.AttemptAsync"/> that the hourly
    /// sweep later uses for retries — so a Locked operator (BR-160) or a missing bank account is
    /// handled identically whether this is the first attempt or the hundredth. A zero-total batch
    /// (every settlement fully absorbed by debt, PD-3) never calls the gateway at all.</summary>
    private async Task DispatchAsync(List<PayoutBatch> batches, DateTimeOffset nowUtc, CancellationToken ct)
    {
        foreach (var batch in batches)
        {
            if (batch.TotalAmount == 0m)
            {
                batch.MarkSettledWithoutTransfer(nowUtc);
                continue;
            }

            await retryProcessor.AttemptAsync(batch, nowUtc, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    internal static string MapOutcome(PayoutTransferOutcome outcome) => outcome switch
    {
        PayoutTransferOutcome.Succeeded => PayoutSettlementAttempt.OutcomeSucceeded,
        PayoutTransferOutcome.Failed => PayoutSettlementAttempt.OutcomeFailed,
        PayoutTransferOutcome.Timeout => PayoutSettlementAttempt.OutcomeTimeout,
        PayoutTransferOutcome.Rejected => PayoutSettlementAttempt.OutcomeRejected,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}