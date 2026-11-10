using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>One booking's input for the daily run, already resolved to its operator and the
/// commission rate snapshot taken at payment time (DAILY-PAYOUT-TRANSFER spec PD-2, PD-3).</summary>
public sealed record EligibleBookingForSettlement(
    long BookingId,
    long TourOperatorUserId,
    string Kind,
    decimal GrossAmount,
    decimal CommissionRatePercent,
    DateTimeOffset OrderKey);

/// <summary>
/// Selects the bookings a daily run must create settlements for (PD-2), and excludes operators
/// that are currently locked (BR-160, PD-9) from the selection entirely — a locked operator's
/// bookings are simply left unselected; they become eligible again on whichever run happens
/// after the operator is unlocked, since no settlement is ever created for them while locked
/// (deviation from a literal reading of the spec, recorded in the plan's Task 8 notes: it avoids
/// having to special-case an "orphan batch-less settlement" and still satisfies PD-9's intent —
/// money is never transferred to a locked operator, and nothing is paid out twice).
///
/// Refund hold periods and the UC-46 payout-request model do not apply here (SRS v11 removed
/// them); eligibility is simply the booking's own payment/cancellation state at the moment the
/// run starts (PD-2).
/// </summary>
public sealed class DailyPayoutEligibilityReader(IApplicationDbContext db)
{
    /// <summary>Selects the bookings whose Awaiting Window (BR-141) has already ended as of
    /// <paramref name="nowUtc"/> — the moment the run itself is executing, not necessarily the
    /// booking's own payment time plus a fixed offset against wall-clock "now".</summary>
    public async Task<IReadOnlyList<EligibleBookingForSettlement>> LoadEligibleBookingsAsync(
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var (candidates, settledKeys) = await LoadCandidatesAsync(cancellationToken);

        var lockedOperatorIds = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.TourOperator && u.Status == AccountStatus.Locked)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        var lockedOperators = lockedOperatorIds.ToHashSet();

        var windowEndCutoff = nowUtc - DailyPayoutClock.AwaitingWindowDuration;

        return candidates
            .Where(c => !settledKeys.Contains((c.BookingId, c.Kind)))
            .Where(c => !lockedOperators.Contains(c.TourOperatorUserId))
            .Where(c => c.OrderKey <= windowEndCutoff) // BR-141: window must have ended
            .OrderBy(r => r.TourOperatorUserId)
            .ThenBy(r => r.OrderKey) // PD-15: debt consumed oldest booking first
            .ToList();
    }

    /// <summary>UC-72 (SRS v12 §3.9.9.1): the bookings that should display as "Awaiting Window" —
    /// paid, not fully refunded, and with no settlement row yet, for any reason (still inside its
    /// 24-hour window, or its operator is locked under BR-160). Unlike
    /// <see cref="LoadEligibleBookingsAsync"/>, this is read-only display, never used to create a
    /// settlement — a run decides eligibility on its own, independently of this list.</summary>
    public async Task<IReadOnlyList<EligibleBookingForSettlement>> LoadAwaitingWindowBookingsAsync(
        CancellationToken cancellationToken)
    {
        var (candidates, settledKeys) = await LoadCandidatesAsync(cancellationToken);

        return candidates
            .Where(c => !settledKeys.Contains((c.BookingId, c.Kind)))
            .OrderBy(r => r.TourOperatorUserId)
            .ThenBy(r => r.OrderKey)
            .ToList();
    }

    private async Task<(List<EligibleBookingForSettlement> Candidates, HashSet<(long BookingId, string Kind)> SettledKeys)>
        LoadCandidatesAsync(CancellationToken cancellationToken)
    {
        var alreadySettled = await db.PayoutSettlements.AsNoTracking()
            .Select(s => new { s.BookingId, s.Kind })
            .ToListAsync(cancellationToken);
        var settledKeys = alreadySettled.Select(x => (x.BookingId, x.Kind)).ToHashSet();

        // Main: a Confirmed, Paid booking that was never cancelled.
        var mainCandidates = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed && b.PaymentStatus == PaymentStatus.Paid)
            .Select(b => new
            {
                b.Id,
                OperatorUserId = b.TourSchedule.Tour.OperatorUserId,
                Gross = b.TotalAmount,
                Rate = db.PaymentTransactions
                    .Where(p => p.BookingId == b.Id && p.Status == PaymentTransactionStatus.Success)
                    .OrderBy(p => p.SucceededAtUtc ?? p.CreatedAtUtc)
                    .Select(p => p.CommissionRateApplied)
                    .FirstOrDefault(),
                OrderKey = db.PaymentTransactions
                    .Where(p => p.BookingId == b.Id && p.Status == PaymentTransactionStatus.Success)
                    .OrderBy(p => p.SucceededAtUtc ?? p.CreatedAtUtc)
                    .Select(p => p.SucceededAtUtc ?? p.CreatedAtUtc)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // Retained (PD-2, BR-142): a Cancelled booking that kept part of its payment — either
        // PartiallyRefunded (some money returned) or still Paid (no refund issued, 0% tier).
        // A fully Refunded booking keeps nothing for the operator and is excluded entirely.
        var retainedCandidates = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Cancelled
                && (b.PaymentStatus == PaymentStatus.PartiallyRefunded || b.PaymentStatus == PaymentStatus.Paid))
            .Select(b => new
            {
                b.Id,
                OperatorUserId = b.TourSchedule.Tour.OperatorUserId,
                TotalPaid = b.TotalAmount,
                RefundedSoFar = db.Refunds
                    .Where(r => r.BookingId == b.Id && r.Status == RefundStatus.Processed)
                    .Select(r => r.Amount)
                    .Sum(),
                Rate = db.PaymentTransactions
                    .Where(p => p.BookingId == b.Id && p.Status == PaymentTransactionStatus.Success)
                    .OrderBy(p => p.SucceededAtUtc ?? p.CreatedAtUtc)
                    .Select(p => p.CommissionRateApplied)
                    .FirstOrDefault(),
                OrderKey = db.PaymentTransactions
                    .Where(p => p.BookingId == b.Id && p.Status == PaymentTransactionStatus.Success)
                    .OrderBy(p => p.SucceededAtUtc ?? p.CreatedAtUtc)
                    .Select(p => p.SucceededAtUtc ?? p.CreatedAtUtc)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var results = new List<EligibleBookingForSettlement>();

        foreach (var row in mainCandidates)
        {
            results.Add(new EligibleBookingForSettlement(
                row.Id, row.OperatorUserId, PayoutSettlementKind.Main, row.Gross,
                row.Rate ?? 0m, row.OrderKey));
        }

        foreach (var row in retainedCandidates)
        {
            var retainedGross = row.TotalPaid - row.RefundedSoFar;
            if (retainedGross <= 0) continue; // fully refunded in substance, nothing retained

            results.Add(new EligibleBookingForSettlement(
                row.Id, row.OperatorUserId, PayoutSettlementKind.Retained, retainedGross,
                row.Rate ?? 0m, row.OrderKey));
        }

        return (results, settledKeys);
    }

    /// <summary>
    /// Outstanding debt the operator OWES TripMate (PD-15, BR-144) — money withheld from a
    /// future payout, e.g. a refund share the operator failed to return under UC-42. That ledger
    /// belongs to the post-trip-refund branch and does not exist in this codebase yet, so this
    /// always returns an empty map for now. It is a named integration point: once that ledger
    /// exists, wire its outstanding-debt query in here without changing
    /// <see cref="DailyPayoutAmountCalculator"/> or the run processor.
    /// </summary>
    public Task<IReadOnlyDictionary<long, decimal>> LoadOutstandingDebtByOperatorAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<long, decimal>>(new Dictionary<long, decimal>());

    /// <summary>Carried-over amounts TripMate still OWES an operator from a past failed batch
    /// (PD-10) and has not yet folded into a new batch. The opposite direction from the debt
    /// above: this money is ADDED to the operator's next batch, not subtracted.</summary>
    public async Task<IReadOnlyList<PayoutSettlementAdjustment>> LoadOutstandingAdjustmentsAsync(long tourOperatorUserId, CancellationToken cancellationToken) =>
        await db.PayoutSettlementAdjustments
            .Where(a => a.TourOperatorUserId == tourOperatorUserId && a.AppliedBatchId == null)
            .ToListAsync(cancellationToken);

    public async Task<bool> IsOperatorLockedAsync(long tourOperatorUserId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Id == tourOperatorUserId)
            .Select(u => u.Status == AccountStatus.Locked)
            .FirstOrDefaultAsync(cancellationToken);
}