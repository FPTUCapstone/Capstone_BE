using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Payouts.DailySettlement;

public enum DailyPayoutRunOutcome
{
    /// <summary>Vietnam-local time has not reached 10:00 yet; nothing to do.</summary>
    NotDueYet,

    /// <summary>A <see cref="PayoutRun"/> already exists for today's Vietnam-local date — this
    /// run already happened, including as a catch-up after a restart (PD-11, PD-16).</summary>
    AlreadyRan,

    /// <summary>The run was performed just now.</summary>
    Ran,
}

/// <summary>
/// DAILY-PAYOUT-TRANSFER Task 6/6A: creates and dispatches the daily settlement run (SRS v11
/// UC-71, UC-74; spec PD-1, PD-2, PD-11, PD-14, PD-16). Intended to be invoked by a background
/// service polling at a short interval (e.g. every few minutes) — <see cref="RunIfDueAsync"/>
/// itself decides whether it is time, and whether today's run already happened, so polling more
/// often than once a day is always safe (idempotent) and is exactly what gives PD-16's
/// restart-catch-up behaviour: whichever poll first notices "past 10:00, no run yet today" does
/// the run, whether that is the scheduled poll or the first poll after a crash/restart.
/// </summary>
public sealed class DailyPayoutRunProcessor(
    IApplicationDbContext db,
    DailyPayoutEligibilityReader reader,
    IPayoutTransferGateway gateway,
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

            var batches = await CreateBatchesAsync(runDate, nowUtc, ct);
            await DispatchAsync(batches, nowUtc, ct);

            run.Complete(clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        return DailyPayoutRunOutcome.Ran;
    }

    /// <summary>Creates one <see cref="PayoutBatch"/> per Tour Operator that has either a newly
    /// eligible booking (PD-2) or an outstanding carried-over amount (PD-10) this run, and one
    /// <see cref="PayoutSettlement"/> per booking (PD-5). Operators currently locked (BR-160) are
    /// skipped entirely by <see cref="DailyPayoutEligibilityReader"/> and are not considered here.</summary>
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
                .Select(e => new DailySettlementInput(e.BookingId, e.Kind, e.GrossAmount, e.CommissionRatePercent))
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

        await FoldCarriedOverAdjustmentsAsync(runDate, nowUtc, batchesByOperator, ct);
        await db.SaveChangesAsync(ct);

        return batchesByOperator.Values.ToList();
    }

    /// <summary>Adds any outstanding carried-over amount (PD-10) to the operator's batch for this
    /// run, creating one if the operator had no newly eligible booking — otherwise a carried-over
    /// amount could be stuck indefinitely waiting for the operator's next booking.</summary>
    private async Task FoldCarriedOverAdjustmentsAsync(
        DateOnly runDate, DateTimeOffset nowUtc, Dictionary<long, PayoutBatch> batchesByOperator, CancellationToken ct)
    {
        var operatorsWithAdjustments = await db.PayoutSettlementAdjustments.AsNoTracking()
            .Where(a => a.AppliedBatchId == null)
            .Select(a => a.TourOperatorUserId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var operatorId in operatorsWithAdjustments)
        {
            if (!batchesByOperator.TryGetValue(operatorId, out var batch))
            {
                batch = PayoutBatch.Create(runDate, operatorId, nowUtc);
                db.PayoutBatches.Add(batch);
                await db.SaveChangesAsync(ct);
                batchesByOperator[operatorId] = batch;
            }

            var adjustments = await reader.LoadOutstandingAdjustmentsAsync(operatorId, ct);
            foreach (var adjustment in adjustments)
            {
                batch.IncludeCarriedOverAmount(adjustment.Amount);
                adjustment.Apply(batch.Id, nowUtc);
            }
        }
    }

    /// <summary>Sends the first transfer attempt for every batch just created (PD-6, PD-7). A
    /// zero-total batch (every settlement fully absorbed by debt, PD-3) never calls the gateway.
    /// Retries beyond this first attempt are the retry sweep's job (PD-7).</summary>
    private async Task DispatchAsync(List<PayoutBatch> batches, DateTimeOffset nowUtc, CancellationToken ct)
    {
        foreach (var batch in batches)
        {
            if (batch.TotalAmount == 0m)
            {
                batch.MarkSettledWithoutTransfer(nowUtc);
                continue;
            }

            batch.MarkProcessing(nowUtc);
            var reference = PayoutGatewayReference.Create(batch.RunDate, batch.TourOperatorUserId, batch.Id);
            var result = await gateway.SendAsync(reference, batch.TourOperatorUserId, batch.TotalAmount, ct);

            db.PayoutSettlementAttempts.Add(PayoutSettlementAttempt.Create(
                batch.Id, batch.AttemptCount, nowUtc, reference,
                MapOutcome(result.Outcome), result.FailureReason));

            // Rejected is treated as a permanent failure (e.g. a bad bank account, PD-8): no
            // point retrying automatically. Failed/Timeout stay Processing for the retry sweep
            // (Task 7), which looks the reference up (PD-7a) before ever resending.
            if (result.Outcome == PayoutTransferOutcome.Succeeded)
                batch.MarkSettled(reference, nowUtc);
            else if (result.Outcome == PayoutTransferOutcome.Rejected)
                batch.MarkFailed(nowUtc);
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
