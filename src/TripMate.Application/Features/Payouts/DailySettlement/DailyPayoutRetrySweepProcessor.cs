using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

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
            batch.MarkSettled(reference, nowUtc);
            return true;
        }

        var pastRetryBudget = batch.AttemptCount > MaxRetries; // attempt 1 is the initial send
        var pastCutoff = DailyPayoutClock.IsPastRetryCutoffFor(batch.RunDate, nowUtc);
        if (lookup.Outcome == PayoutTransferOutcome.Rejected || pastRetryBudget || pastCutoff)
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
}
