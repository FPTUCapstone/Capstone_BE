using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>
/// DAILY-PAYOUT-TRANSFER Task 7/7A: retries a batch still in Processing status, hourly, up to
/// the configured attempt budget and same-day cutoff (spec decision 1 and 6: 3 retries, last one
/// at 13:00). Always looks the batch's gateway reference up before ever resending (PD-7a) — this
/// lookup both decides whether a retry is needed AND serves as the run's reconciliation pass
/// (PD-17): if the gateway reports the transfer already succeeded, the batch is settled from
/// that lookup alone, with no second transfer ever sent.
///
/// Simplification versus the spec's data model: a pure confirmatory lookup is not written to
/// <see cref="PayoutSettlementAttempt"/> or counted against the retry budget — only an actual
/// resend (a second <c>SendAsync</c>) is. This keeps "3 retries" meaning three more transfer
/// attempts, not three status checks, which is what BR-145/PD-7 are actually bounding.
/// </summary>
public sealed class DailyPayoutRetrySweepProcessor(
    IApplicationDbContext db,
    IPayoutTransferGateway gateway,
    IDateTimeProvider clock)
{
    /// <summary>1 initial send (done by <see cref="DailyPayoutRunProcessor"/>) + up to this many
    /// retries (spec decision 1: N = 3).</summary>
    public const int MaxRetries = 3;

    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        var processingBatches = await db.PayoutBatches
            .Include(b => b.Settlements)
            .Where(b => b.Status == PayoutSettlementStatus.Processing)
            .ToListAsync(cancellationToken);

        var acted = 0;
        foreach (var batch in processingBatches)
        {
            if (await ProcessOneAsync(batch, nowUtc, cancellationToken))
                acted++;
        }

        if (acted > 0)
            await db.SaveChangesAsync(cancellationToken);

        return acted;
    }

    private async Task<bool> ProcessOneAsync(PayoutBatch batch, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var dueForRetry = batch.LastAttemptAtUtc is null || nowUtc - batch.LastAttemptAtUtc.Value >= RetryInterval;
        if (!dueForRetry)
            return false;

        var reference = PayoutGatewayReference.Create(batch.RunDate, batch.TourOperatorUserId, batch.Id);
        var lookup = await gateway.LookupAsync(reference, ct); // PD-7a + PD-17, always before acting

        if (lookup.Outcome == PayoutTransferOutcome.Succeeded)
        {
            await RecordDiscrepancyAsync(batch, nowUtc, PayoutReconciliationItem.ObservedOutcomeSucceeded, reference, ct);
            batch.MarkSettled(reference, nowUtc);
            return true;
        }

        var pastRetryBudget = batch.AttemptCount > MaxRetries; // attempt 1 is the initial send
        var pastCutoff = DailyPayoutClock.IsPastRetryCutoffFor(batch.RunDate, nowUtc);
        if (lookup.Outcome == PayoutTransferOutcome.Rejected)
        {
            await RecordDiscrepancyAsync(batch, nowUtc, PayoutReconciliationItem.ObservedOutcomeRejected, reference, ct);
            batch.MarkFailed(nowUtc);
            return true;
        }
        if (pastRetryBudget || pastCutoff)
        {
            batch.MarkFailed(nowUtc);
            return true;
        }

        batch.RecordRetryAttempt(nowUtc);
        var sendResult = await gateway.SendAsync(reference, batch.TourOperatorUserId, batch.TotalAmount, ct);
        db.PayoutSettlementAttempts.Add(PayoutSettlementAttempt.Create(
            batch.Id, batch.AttemptCount, nowUtc, reference,
            DailyPayoutRunProcessor.MapOutcome(sendResult.Outcome), sendResult.FailureReason));

        if (sendResult.Outcome == PayoutTransferOutcome.Succeeded)
            batch.MarkSettled(reference, nowUtc);
        else if (sendResult.Outcome == PayoutTransferOutcome.Rejected)
            batch.MarkFailed(nowUtc);
        // Failed/Timeout: stays Processing, picked up again next hour by this sweep.

        return true;
    }

    /// <summary>Task 7A (PD-17): the PD-7a lookup just revealed the batch's real outcome differs
    /// from what TripMate still believed (<see cref="PayoutSettlementStatus.Processing"/>) — a
    /// plain Failed/Timeout lookup is unresolved, not a discrepancy, and never reaches here.
    /// Records a <see cref="PayoutReconciliationItem"/>, an audit log entry, and a push
    /// notification to every active Administrator (same fan-out pattern as
    /// <c>RefundProcessor.AddManualReviewNotificationsAsync</c>).</summary>
    private async Task RecordDiscrepancyAsync(
        PayoutBatch batch, DateTimeOffset nowUtc, string observedOutcome, string observedReference, CancellationToken ct)
    {
        db.PayoutReconciliationItems.Add(PayoutReconciliationItem.Create(
            batch.Id, nowUtc, PayoutSettlementStatus.Processing, observedOutcome, observedReference,
            note: $"Gateway lookup reported {observedOutcome} for batch {batch.Id}, still Processing in TripMate."));

        db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            actorUserId: null,
            actionType: AuditActionTypes.PayoutReconciliationDiscrepancyDetected,
            affectedEntity: AuditEntityTypes.PayoutBatch,
            affectedEntityId: batch.Id,
            createdAtUtc: nowUtc,
            result: AuditOutcome.Success,
            reason: $"Gateway lookup reported {observedOutcome}"));

        var administratorIds = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Administrator && u.Status == AccountStatus.Active)
            .Select(u => u.Id)
            .ToListAsync(ct);
        foreach (var administratorId in administratorIds)
        {
            db.Notifications.Add(new Notification
            {
                UserId = administratorId,
                Channel = NotificationChannel.Push,
                Type = "PayoutReconciliationDiscrepancyDetected",
                Title = "Payout reconciliation discrepancy",
                Body = $"Batch {batch.Id} (Tour Operator {batch.TourOperatorUserId}): gateway reports {observedOutcome}, review required.",
                RelatedEntityType = AuditEntityTypes.PayoutBatch,
                RelatedEntityId = batch.Id,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = nowUtc,
            });
        }
    }
}