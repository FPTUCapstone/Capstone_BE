using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>One booking's input for the daily run, already resolved to its operator and the
/// commission rate snapshot taken at payment time (DAILY-PAYOUT-TRANSFER spec v6 PD-2, PD-3).</summary>
public sealed record EligibleBookingForSettlement(
    long BookingId,
    long TourOperatorUserId,
    string Kind,
    decimal PaidAmount,
    decimal RefundAmount,
    decimal CommissionRatePercent,
    DateTimeOffset OrderKey,
    decimal SettledToOperatorAmount = 0m,
    decimal OperatorReturnShare = 0m);

/// <summary>
/// Selects the bookings a daily run must create settlements for (PD-2). Scheduling does not
/// check the operator's lock status (BR-160) — a Locked operator's booking is still scheduled on
/// time; only the transfer (in <see cref="DailyPayoutRunProcessor"/>/<see cref="DailyPayoutRetrySweepProcessor"/>)
/// is deferred while locked, with the settlement left in <c>Scheduled</c> status.
///
/// At most one Advance and one Final settlement exists for a completed booking (PD-5): Advance
/// once the 24-hour Awaiting Window has ended (BR-141, PD-1A), Final at the first 10:00 run after
/// the tour ends (Q6). A cancelled booking has instead at most one Cancellation settlement after
/// its refund has succeeded: 90% of the non-refunded cancellation fee goes to the Tour Operator.
/// </summary>
public sealed class DailyPayoutEligibilityReader(IApplicationDbContext db)
{
    /// <summary>Selects the bookings eligible for a settlement as of <paramref name="nowUtc"/> —
    /// the moment the run itself is executing.</summary>
    public async Task<IReadOnlyList<EligibleBookingForSettlement>> LoadEligibleBookingsAsync(
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var (candidates, settledKeys) = await LoadCandidatesAsync(nowUtc, cancellationToken);

        var windowEndCutoff = nowUtc - DailyPayoutClock.AwaitingWindowDuration;

        return candidates
            .Where(c => !settledKeys.Contains((c.BookingId, c.Kind)))
            // PD-1A: an Advance candidate only becomes eligible once its 24-hour Awaiting Window
            // has ended; a Final candidate carries no such window (its OrderKey is already the
            // tour's end time, strictly in the past by construction above).
            .Where(c => c.Kind != PayoutSettlementKind.Advance || c.OrderKey <= windowEndCutoff)
            .OrderBy(r => r.TourOperatorUserId)
            .ThenBy(r => r.OrderKey) // PD-15: debt consumed oldest booking first
            .ToList();
    }

    /// <summary>UC-72: the bookings that should display as "Awaiting Window" — paid, not
    /// cancelled, with no Advance settlement yet, for any reason (still inside its 24-hour
    /// window, or its operator is locked). Unlike <see cref="LoadEligibleBookingsAsync"/>, this is
    /// read-only display, never used to create a settlement.</summary>
    public async Task<IReadOnlyList<EligibleBookingForSettlement>> LoadAwaitingWindowBookingsAsync(
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var (candidates, settledKeys) = await LoadCandidatesAsync(nowUtc, cancellationToken);

        return candidates
            .Where(c => c.Kind == PayoutSettlementKind.Advance && !settledKeys.Contains((c.BookingId, c.Kind)))
            .OrderBy(r => r.TourOperatorUserId)
            .ThenBy(r => r.OrderKey)
            .ToList();
    }

    /// <summary>Logs fully-refunded cancellations, which have no cancellation fee and therefore
    /// no payout. Partially- or non-refunded cancellations are handled by the Cancellation
    /// settlement candidate instead. Idempotent: a booking already logged is skipped.</summary>
    public async Task LogCancelledExclusionsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var alreadyLogged = await db.AuditLogs.AsNoTracking()
            .Where(a => a.ActionType == AuditActionTypes.PayoutExclusionCancelledBeforeRun)
            .Select(a => a.AffectedEntityId)
            .ToListAsync(cancellationToken);
        var loggedBookingIds = alreadyLogged.Where(id => id is not null).Select(id => id!.Value).ToHashSet();

        var cancelledBookings = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Cancelled
                && (b.PaymentStatus == PaymentStatus.Paid || b.PaymentStatus == PaymentStatus.PartiallyRefunded || b.PaymentStatus == PaymentStatus.Refunded)
                && db.PaymentTransactions.Any(p => p.BookingId == b.Id && p.Status == PaymentTransactionStatus.Success))
            .Select(b => new { b.Id, b.TotalAmount })
            .ToListAsync(cancellationToken);

        var cancelledIds = cancelledBookings.Select(b => b.Id).ToList();
        var processedRefunds = cancelledIds.Count == 0
            ? new Dictionary<long, decimal>()
            : await db.Refunds.AsNoTracking()
                .Where(r => cancelledIds.Contains(r.BookingId) && r.Status == RefundStatus.Processed)
                .GroupBy(r => r.BookingId)
                .Select(g => new { BookingId = g.Key, Amount = g.Sum(r => r.Amount) })
                .ToDictionaryAsync(x => x.BookingId, x => x.Amount, cancellationToken);

        foreach (var booking in cancelledBookings)
        {
            if (loggedBookingIds.Contains(booking.Id) || processedRefunds.GetValueOrDefault(booking.Id) < booking.TotalAmount)
                continue;

            db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                actorUserId: null,
                AuditActionTypes.PayoutExclusionCancelledBeforeRun,
                AuditEntityTypes.Booking,
                booking.Id,
                nowUtc,
                AuditOutcome.Success,
                reason: "fully refunded cancellation"));
        }
    }

    private async Task<(List<EligibleBookingForSettlement> Candidates, HashSet<(long BookingId, string Kind)> SettledKeys)>
        LoadCandidatesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var alreadySettled = await db.PayoutSettlements.AsNoTracking()
            .Select(s => new { s.BookingId, s.Kind })
            .ToListAsync(cancellationToken);
        var settledKeys = alreadySettled.Select(x => (x.BookingId, x.Kind)).ToHashSet();

        // A Confirmed, Paid, non-cancelled booking (PD-2). Both Advance and Final draw from the
        // same underlying booking; a booking can carry at most one row of each kind (PD-5).
        var rows = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed && b.PaymentStatus == PaymentStatus.Paid)
            .Select(b => new
            {
                b.Id,
                OperatorUserId = b.TourSchedule.Tour.OperatorUserId,
                PaidAmount = b.TotalAmount,
                TourEndAtUtc = b.TourSchedule.EndAtUtc,
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

        // BR-142: the Final (20%) share is computed on (PaidAmount − already-processed refunds)
        // for that booking — a partial refund issued before the tour ends must not inflate the
        // operator's Final payout. Advance never applies this: it is paid 24h after payment,
        // almost always before any refund exists, and PD-3's 70% rate is fixed regardless.
        var bookingIds = rows.Select(r => r.Id).ToList();
        var refundedByBooking = bookingIds.Count == 0
            ? new Dictionary<long, decimal>()
            : await db.Refunds.AsNoTracking()
                .Where(r => r.Status == RefundStatus.Processed && bookingIds.Contains(r.BookingId))
                .GroupBy(r => r.BookingId)
                .Select(g => new { BookingId = g.Key, Amount = g.Sum(x => x.Amount) })
                .ToDictionaryAsync(x => x.BookingId, x => x.Amount, cancellationToken);

        var results = new List<EligibleBookingForSettlement>();

        foreach (var row in rows)
        {
            // Advance candidate (PD-2, PD-3): the 24-hour Awaiting Window (PD-1A) is applied by
            // the caller, not here — LoadAwaitingWindowBookingsAsync needs the ones still inside
            // it too (display purposes), while LoadEligibleBookingsAsync filters them out.
            results.Add(new EligibleBookingForSettlement(
                row.Id, row.OperatorUserId, PayoutSettlementKind.Advance,
                row.PaidAmount, RefundAmount: 0m, row.Rate ?? 0m, row.OrderKey));

            // Final (Q6): at the first 10:00 run strictly after the tour's own end.
            if (row.TourEndAtUtc <= nowUtc)
            {
                var refunded = refundedByBooking.GetValueOrDefault(row.Id, 0m);
                results.Add(new EligibleBookingForSettlement(
                    row.Id, row.OperatorUserId, PayoutSettlementKind.Final,
                    row.PaidAmount, RefundAmount: refunded, row.Rate ?? 0m, row.TourEndAtUtc));
            }
        }

        results.AddRange(await LoadCancellationCandidatesAsync(nowUtc, cancellationToken));

        return (results, settledKeys);
    }

    private async Task<IReadOnlyList<EligibleBookingForSettlement>> LoadCancellationCandidatesAsync(
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var cancelled = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Cancelled
                && db.PaymentTransactions.Any(p => p.BookingId == b.Id && p.Status == PaymentTransactionStatus.Success))
            .Select(b => new
            {
                b.Id,
                OperatorUserId = b.TourSchedule.Tour.OperatorUserId,
                PaidAmount = b.TotalAmount,
                CancelledAtUtc = b.CancelledAtUtc ?? nowUtc,
                Rate = db.PaymentTransactions
                    .Where(p => p.BookingId == b.Id && p.Status == PaymentTransactionStatus.Success)
                    .OrderBy(p => p.SucceededAtUtc ?? p.CreatedAtUtc)
                    .Select(p => p.CommissionRateApplied)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        if (cancelled.Count == 0)
            return [];

        var bookingIds = cancelled.Select(b => b.Id).ToList();
        var refundRows = await db.Refunds.AsNoTracking()
            .Where(r => bookingIds.Contains(r.BookingId))
            .Select(r => new { r.BookingId, r.Amount, r.OperatorReturnShare, r.Status })
            .ToListAsync(cancellationToken);
        var settlements = await db.PayoutSettlements.AsNoTracking()
            .Where(s => bookingIds.Contains(s.BookingId))
            .Select(s => new { s.BookingId, s.Kind, s.Status, s.PayoutAmount })
            .ToListAsync(cancellationToken);

        var candidates = new List<EligibleBookingForSettlement>();
        foreach (var booking in cancelled)
        {
            var refunds = refundRows.Where(r => r.BookingId == booking.Id).ToList();
            // A refund request must finish before its cancellation fee can be settled. A booking
            // cancelled with a 0% tier has no Refund row and is ready immediately.
            if (refunds.Count > 0 && refunds.Any(r => r.Status != RefundStatus.Processed))
                continue;

            decimal refundedAmount = refunds.Sum(r => r.Amount);
            if (refundedAmount >= booking.PaidAmount)
                continue;

            var bookingSettlements = settlements.Where(s => s.BookingId == booking.Id).ToList();
            if (bookingSettlements.Any(s => s.Kind == PayoutSettlementKind.Cancellation)
                || bookingSettlements.Any(s => s.Status is PayoutSettlementStatus.Scheduled or PayoutSettlementStatus.Processing))
                continue;

            decimal settledToOperator = bookingSettlements
                .Where(s => s.Status == PayoutSettlementStatus.Settled)
                .Sum(s => s.PayoutAmount);
            decimal operatorReturnShare = refunds.Sum(r => r.OperatorReturnShare ?? 0m);

            candidates.Add(new EligibleBookingForSettlement(
                booking.Id, booking.OperatorUserId, PayoutSettlementKind.Cancellation,
                booking.PaidAmount, refundedAmount, booking.Rate ?? 0m, booking.CancelledAtUtc,
                settledToOperator, operatorReturnShare));
        }

        return candidates;
    }

    /// <summary>
    /// Outstanding debt the operator OWES TripMate (PD-15, BR-144, BR-188) — written by the
    /// post-trip-refund branch (out of scope here) whenever an operator fails to return a refund
    /// share under UC-42. Sums every <see cref="TourOperatorDebt"/> row's
    /// <see cref="TourOperatorDebt.OutstandingAmount"/> per operator.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, decimal>> LoadOutstandingDebtByOperatorAsync(CancellationToken cancellationToken)
    {
        var rows = await db.TourOperatorDebts.AsNoTracking()
            .Where(d => d.RecoveredAmount < d.Amount)
            .Select(d => new { d.TourOperatorUserId, d.Amount, d.RecoveredAmount })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(d => d.TourOperatorUserId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Amount - d.RecoveredAmount));
    }

    /// <summary>BR-189: the operators whose oldest unrecovered debt (by <see cref="TourOperatorDebt.IncurredAtUtc"/>)
    /// has been outstanding for at least 15 days as of <paramref name="nowUtc"/>, and who are not
    /// already locked.</summary>
    public async Task<IReadOnlyList<long>> LoadOperatorsOverdueForAutoLockAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var cutoff = nowUtc - DailyPayoutAutoLock.UnrecoveredDebtGracePeriod;

        var lockedOperatorIds = await db.Users.AsNoTracking()
            .Where(u => u.Status == AccountStatus.Locked)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        var locked = lockedOperatorIds.ToHashSet();

        var overdue = await db.TourOperatorDebts.AsNoTracking()
            .Where(d => d.RecoveredAmount < d.Amount && d.IncurredAtUtc <= cutoff)
            .Select(d => d.TourOperatorUserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return overdue.Where(id => !locked.Contains(id)).ToList();
    }

    public async Task<bool> IsOperatorLockedAsync(long tourOperatorUserId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Id == tourOperatorUserId)
            .Select(u => u.Status == AccountStatus.Locked)
            .FirstOrDefaultAsync(cancellationToken);
}
