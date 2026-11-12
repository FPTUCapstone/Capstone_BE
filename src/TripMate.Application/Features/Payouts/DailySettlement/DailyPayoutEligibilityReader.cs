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
    DateTimeOffset OrderKey);

/// <summary>
/// Selects the bookings a daily run must create settlements for (PD-2). Scheduling does not
/// check the operator's lock status (BR-160) — a Locked operator's booking is still scheduled on
/// time; only the transfer (in <see cref="DailyPayoutRunProcessor"/>/<see cref="DailyPayoutRetrySweepProcessor"/>)
/// is deferred while locked, with the settlement left in <c>Scheduled</c> status.
///
/// At most one Advance and one Final settlement exists per booking (PD-5): Advance once the
/// 24-hour Awaiting Window has ended (BR-141, PD-1A), Final at the first 10:00 run after the
/// tour ends (Q6). A booking cancelled before a run gets no settlement for that run at all
/// (PD-2A) — it simply fails the "not Cancelled" condition below; <see cref="DailyPayoutRunProcessor"/>
/// logs that exclusion separately.
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

    /// <summary>PD-2A: logs, once per booking, every booking that is Cancelled and was never
    /// settled (neither Advance nor Final) — a run never creates a settlement for it, but the
    /// exclusion itself must be auditable. Idempotent: a booking already logged is skipped.</summary>
    public async Task LogCancelledExclusionsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var alreadyLogged = await db.AuditLogs.AsNoTracking()
            .Where(a => a.ActionType == AuditActionTypes.PayoutExclusionCancelledBeforeRun)
            .Select(a => a.AffectedEntityId)
            .ToListAsync(cancellationToken);
        var loggedBookingIds = alreadyLogged.Where(id => id is not null).Select(id => id!.Value).ToHashSet();

        var settledBookingIds = await db.PayoutSettlements.AsNoTracking()
            .Select(s => s.BookingId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var settledIds = settledBookingIds.ToHashSet();

        var cancelledPaidBookingIds = await db.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Cancelled
                && (b.PaymentStatus == PaymentStatus.Paid || b.PaymentStatus == PaymentStatus.PartiallyRefunded || b.PaymentStatus == PaymentStatus.Refunded)
                && db.PaymentTransactions.Any(p => p.BookingId == b.Id && p.Status == PaymentTransactionStatus.Success))
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        foreach (var bookingId in cancelledPaidBookingIds)
        {
            if (loggedBookingIds.Contains(bookingId) || settledIds.Contains(bookingId))
                continue;

            db.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                actorUserId: null,
                AuditActionTypes.PayoutExclusionCancelledBeforeRun,
                AuditEntityTypes.Booking,
                bookingId,
                nowUtc,
                AuditOutcome.Success,
                reason: "cancelled before run"));
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
                results.Add(new EligibleBookingForSettlement(
                    row.Id, row.OperatorUserId, PayoutSettlementKind.Final,
                    row.PaidAmount, RefundAmount: 0m, row.Rate ?? 0m, row.TourEndAtUtc));
            }
        }

        return (results, settledKeys);
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