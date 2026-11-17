using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>Outcome of <see cref="DailyPayoutRetrySweepProcessor.ManualRetryAsync"/> (UC-73
/// [Retry Transfer]) — distinguishes a refused attempt (precondition failed, batch untouched,
/// surfaced to the Administrator as a 409) from an attempt that was actually sent.</summary>
public enum ManualRetryOutcome
{
    Settled,
    FailedAfterRetry,
    RefusedOperatorLocked,
    RefusedMissingBankAccount,
}

/// <summary>
/// DAILY-PAYOUT-TRANSFER spec v7 (SRS rev10-6, 3.10.5 UC-74): attempts every <see cref="PayoutBatch"/>
/// that is due for a gateway attempt — a batch stays <c>Scheduled</c> between attempts (BR-145's
/// hourly retry), while its <see cref="PayoutBatch.TourOperatorUserId"/> is Locked (BR-160), or
/// while no attempt has been made yet. <see cref="AttemptAsync"/> is the single place that talks
/// to the gateway; both <see cref="DailyPayoutRunProcessor"/> (the very first attempt, right after
/// a batch is scheduled) and <see cref="SweepAsync"/> (every later hourly attempt) call it, so a
/// batch created today and a batch left over from a day the operator was locked go through
/// exactly the same rules.
///
/// PD-7a: before any attempt beyond the first, the gateway reference is looked up before ever
/// resending — this lookup also serves as the run's reconciliation pass (PD-17): if the gateway
/// reports the transfer already succeeded, the batch is settled from that lookup alone.
/// </summary>
public sealed class DailyPayoutRetrySweepProcessor(
    IApplicationDbContext db,
    IPayoutTransferGateway gateway,
    IDateTimeProvider clock,
    DailyPayoutEligibilityReader eligibilityReader)
{
    /// <summary>1 initial send + up to this many retries (BR-145: N = 3).</summary>
    public const int MaxRetries = 3;

    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    /// <summary>Attempts every <c>Scheduled</c> batch that is due (never attempted, or its last
    /// attempt was at least <see cref="RetryInterval"/> ago) — covers both the hourly retry of a
    /// batch already in flight and the catch-up of a batch left over from a day its operator was
    /// Locked (BR-160), once that operator is Active again.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var nowUtc = clock.UtcNow;
        var scheduledBatches = await db.PayoutBatches
            .Include(b => b.Settlements)
            .Where(b => b.Status == PayoutSettlementStatus.Scheduled)
            .ToListAsync(cancellationToken);

        var acted = 0;
        foreach (var batch in scheduledBatches)
        {
            var dueForAttempt = batch.LastAttemptAtUtc is null || nowUtc - batch.LastAttemptAtUtc.Value >= RetryInterval;
            if (!dueForAttempt)
                continue;

            if (await AttemptAsync(batch, nowUtc, cancellationToken))
                acted++;
        }

        if (acted > 0)
            await db.SaveChangesAsync(cancellationToken);

        return acted;
    }

    /// <summary>Attempts one <c>Scheduled</c> batch right now — unconditionally, without checking
    /// <see cref="PayoutBatch.LastAttemptAtUtc"/> (the caller, <see cref="DailyPayoutRunProcessor"/>
    /// for a batch it just created, or <see cref="SweepAsync"/> for one that is due, is responsible
    /// for deciding whether this is the right moment). Returns false, with no state change, when
    /// the operator is currently Locked (BR-160) — the batch stays Scheduled untouched.</summary>
    public async Task<bool> AttemptAsync(PayoutBatch batch, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (batch.Status != PayoutSettlementStatus.Scheduled)
            throw new InvalidOperationException($"Cannot attempt a batch in status {batch.Status}.");

        if (await eligibilityReader.IsOperatorLockedAsync(batch.TourOperatorUserId, ct))
            return false; // BR-160: stays Scheduled; the next sweep after unlock retries it.

        if (!await HasBeneficiaryBankAccountAsync(batch.TourOperatorUserId, ct))
        {
            batch.MarkFailedWithoutAttempt(nowUtc); // SRS 3.10.5, 1.b1
            await NotifyAsync(batch.TourOperatorUserId, "PayoutMissingBankAccount", "Payout transfer could not be sent",
                $"Batch {batch.Id}: no beneficiary bank account on file. Please add one in your profile.", batch, nowUtc); // MSG146
            return true;
        }

        var reference = PayoutGatewayReference.Create(batch.RunDate, batch.TourOperatorUserId, batch.Id);
        var wasAlreadyAttempted = batch.AttemptCount > 0;

        if (wasAlreadyAttempted)
        {
            var lookup = await gateway.LookupAsync(reference, ct); // PD-7a + PD-17
            if (lookup.Outcome == PayoutTransferOutcome.Succeeded)
            {
                await RecordDiscrepancyAsync(batch, nowUtc, PayoutReconciliationItem.ObservedOutcomeSucceeded, reference, ct);
                batch.MarkSettled(reference, nowUtc); // no fresh attempt consumed — the past send already succeeded.
                return true;
            }
            if (lookup.Outcome == PayoutTransferOutcome.Rejected)
            {
                await RecordDiscrepancyAsync(batch, nowUtc, PayoutReconciliationItem.ObservedOutcomeRejected, reference, ct);
                batch.MarkFailed(nowUtc);
                await NotifyAdministratorsAsync(batch, nowUtc,
                    "PayoutSettlementFailed", "Payout transfer failed",
                    $"Batch {batch.Id} (Tour Operator {batch.TourOperatorUserId}) failed after all retries. The settlement needs review.");
                return true;
            }
            // Otherwise (Failed/Timeout): unresolved, fall through to resend.
        }

        batch.MarkProcessing(nowUtc);
        var sendResult = await gateway.SendAsync(reference, batch.TourOperatorUserId, batch.TotalAmount, ct);
        db.PayoutSettlementAttempts.Add(PayoutSettlementAttempt.Create(
            batch.Id, batch.AttemptCount, nowUtc, reference,
            DailyPayoutRunProcessor.MapOutcome(sendResult.Outcome), sendResult.FailureReason));

        if (sendResult.Outcome == PayoutTransferOutcome.Succeeded)
        {
            batch.MarkSettled(reference, nowUtc);
        }
        else if (sendResult.Outcome == PayoutTransferOutcome.Rejected)
        {
            batch.MarkFailed(nowUtc);
            await NotifyAdministratorsAsync(batch, nowUtc,
                "PayoutSettlementFailed", "Payout transfer failed",
                $"Batch {batch.Id} (Tour Operator {batch.TourOperatorUserId}) failed after all retries. The settlement needs review.");
        }
        else if (batch.AttemptCount >= MaxRetries + 1) // this was the last allowed attempt (BR-145)
        {
            batch.MarkFailed(nowUtc);
            await NotifyAdministratorsAsync(batch, nowUtc,
                "PayoutSettlementFailed", "Payout transfer failed",
                $"Batch {batch.Id} (Tour Operator {batch.TourOperatorUserId}) exhausted its automatic retries and needs manual action."); // BR-172, MSG176
        }
        else
        {
            batch.ReturnToScheduledAfterFailedAttempt(nowUtc); // SRS 3.10.5, 3.a1
            await NotifyAdministratorsAsync(batch, nowUtc,
                "PayoutRetryScheduled", "Payout transfer will be retried",
                $"The transfer of {batch.TotalAmount:0} VND failed and will be retried in 1 hour (batch {batch.Id})."); // MSG175
        }

        return true;
    }

    /// <summary>UC-73 [Retry Transfer] (SRS 3.10.6, Normal Flow: "the system submits the transfer
    /// once ... (UC-74 rules apply)") — a one-shot Administrator-triggered resend of a
    /// <c>SettlementFailed</c> batch, with the same amount (idempotent reference). "UC-74 rules
    /// apply" means this inherits <see cref="AttemptAsync"/>'s own preconditions: still refuse
    /// while the Tour Operator is Locked (BR-160) or missing a bank account (MSG146), and still do
    /// the PD-7a lookup before ever resending (the batch already has at least one prior attempt by
    /// definition, having reached SettlementFailed). Unlike the automatic budget in
    /// <see cref="AttemptAsync"/>, a failure after an actual resend goes straight back to
    /// <c>SettlementFailed</c> (MSG176) rather than waiting for another hourly attempt — this is
    /// the Administrator's own single retry, not a step of the automatic 3-retry sequence. Does
    /// not call <c>SaveChangesAsync</c>; the caller controls the transaction.</summary>
    public async Task<ManualRetryOutcome> ManualRetryAsync(PayoutBatch batch, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (batch.Status != PayoutSettlementStatus.SettlementFailed)
            throw new InvalidOperationException($"Cannot retry a batch in status {batch.Status}.");

        if (await eligibilityReader.IsOperatorLockedAsync(batch.TourOperatorUserId, ct))
            return ManualRetryOutcome.RefusedOperatorLocked; // BR-160; batch stays SettlementFailed untouched.

        if (!await HasBeneficiaryBankAccountAsync(batch.TourOperatorUserId, ct))
            return ManualRetryOutcome.RefusedMissingBankAccount; // MSG146; batch stays SettlementFailed untouched.

        var reference = PayoutGatewayReference.Create(batch.RunDate, batch.TourOperatorUserId, batch.Id);

        // PD-7a: the batch already has at least one attempt (it reached SettlementFailed) — look
        // up the real outcome before ever resending, exactly as AttemptAsync does for any retry
        // beyond the first (PayoutBatch.Retry's own doc comment calls this out as the caller's
        // responsibility).
        var lookup = await gateway.LookupAsync(reference, ct);
        if (lookup.Outcome == PayoutTransferOutcome.Succeeded)
        {
            await RecordDiscrepancyAsync(batch, nowUtc, PayoutReconciliationItem.ObservedOutcomeSucceeded, reference, ct);
            batch.MarkSettled(reference, nowUtc); // no fresh attempt consumed — the past send already succeeded.
            return ManualRetryOutcome.Settled;
        }
        if (lookup.Outcome == PayoutTransferOutcome.Rejected)
        {
            await RecordDiscrepancyAsync(batch, nowUtc, PayoutReconciliationItem.ObservedOutcomeRejected, reference, ct);
            batch.MarkFailed(nowUtc); // confirms the already-failed state; no fresh attempt consumed.
            return ManualRetryOutcome.FailedAfterRetry;
        }
        // Otherwise (Failed/Timeout): unresolved, fall through to resend.

        batch.Retry(nowUtc); // SettlementFailed -> Processing
        var sendResult = await gateway.SendAsync(reference, batch.TourOperatorUserId, batch.TotalAmount, ct);
        db.PayoutSettlementAttempts.Add(PayoutSettlementAttempt.Create(
            batch.Id, batch.AttemptCount, nowUtc, reference,
            DailyPayoutRunProcessor.MapOutcome(sendResult.Outcome), sendResult.FailureReason));

        if (sendResult.Outcome == PayoutTransferOutcome.Succeeded)
        {
            batch.MarkSettled(reference, nowUtc);
            return ManualRetryOutcome.Settled;
        }

        batch.MarkFailed(nowUtc); // SRS 3.10.6, 4.a1
        await NotifyAdministratorsAsync(batch, nowUtc,
            "PayoutSettlementFailed", "Payout retry failed",
            $"The retried transfer for batch {batch.Id} (Tour Operator {batch.TourOperatorUserId}) failed again."); // MSG176
        return ManualRetryOutcome.FailedAfterRetry;
    }

    private async Task<bool> HasBeneficiaryBankAccountAsync(long tourOperatorUserId, CancellationToken ct)
    {
        var operatorProfile = await db.OperatorProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == tourOperatorUserId, ct);
        return operatorProfile is not null && operatorProfile.HasBeneficiaryBankAccount;
    }

    /// <summary>BR-172, MSG176/MSG175: notifies every active Administrator.</summary>
    private async Task NotifyAdministratorsAsync(PayoutBatch batch, DateTimeOffset nowUtc, string type, string title, string body)
    {
        var administratorIds = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Administrator && u.Status == AccountStatus.Active)
            .Select(u => u.Id)
            .ToListAsync();
        foreach (var administratorId in administratorIds)
            NotifyOne(administratorId, type, title, body, batch, nowUtc);
    }

    private Task NotifyAsync(long? userId, string type, string title, string body, PayoutBatch batch, DateTimeOffset nowUtc)
    {
        if (userId is { } id)
            NotifyOne(id, type, title, body, batch, nowUtc);
        return Task.CompletedTask;
    }

    private void NotifyOne(long userId, string type, string title, string body, PayoutBatch batch, DateTimeOffset nowUtc) =>
        db.Notifications.Add(new Notification
        {
            UserId = userId,
            Channel = NotificationChannel.Push,
            Type = type,
            Title = title,
            Body = body,
            RelatedEntityType = AuditEntityTypes.PayoutBatch,
            RelatedEntityId = batch.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc,
        });

    /// <summary>PD-17: the PD-7a lookup just revealed the batch's real outcome differs from what
    /// TripMate still believed (Scheduled, about to be attempted again) — records a
    /// <see cref="PayoutReconciliationItem"/> and an audit log entry, and alerts Administrators.</summary>
    private async Task RecordDiscrepancyAsync(
        PayoutBatch batch, DateTimeOffset nowUtc, string observedOutcome, string observedReference, CancellationToken ct)
    {
        db.PayoutReconciliationItems.Add(PayoutReconciliationItem.Create(
            batch.Id, nowUtc, PayoutSettlementStatus.Scheduled, observedOutcome, observedReference,
            note: $"Gateway lookup reported {observedOutcome} for batch {batch.Id}, still Scheduled in TripMate."));

        db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            actorUserId: null,
            actionType: AuditActionTypes.PayoutReconciliationDiscrepancyDetected,
            affectedEntity: AuditEntityTypes.PayoutBatch,
            affectedEntityId: batch.Id,
            createdAtUtc: nowUtc,
            result: AuditOutcome.Success,
            reason: $"Gateway lookup reported {observedOutcome}"));

        await NotifyAdministratorsAsync(batch, nowUtc,
            "PayoutReconciliationDiscrepancyDetected", "Payout reconciliation discrepancy",
            $"Batch {batch.Id} (Tour Operator {batch.TourOperatorUserId}): gateway reports {observedOutcome}, review required.");
    }
}