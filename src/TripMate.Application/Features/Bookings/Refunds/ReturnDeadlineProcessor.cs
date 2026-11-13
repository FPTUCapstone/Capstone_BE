using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// BR-144 (SRS 3.8.4.3, spec v1.2): a Tour Operator's BR-139 return share not fully received
/// within 3 working days of confirmation becomes a pending debt, offset against the operator's
/// next settlements (PD-15, already wired on develop by DAILY-PAYOUT-TRANSFER). This only
/// records the debt — it never changes the refund's own status, which still waits for
/// <see cref="Refund.ReportReturn"/>/<see cref="Refund.RecordReturnReceived"/> before UC-67.
/// </summary>
public sealed class ReturnDeadlineProcessor(
    IApplicationDbContext dbContext, IDateTimeProvider clock, ReturnDeadlineOptions options)
{
    public async Task<int> RecordOverdueDebtsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        List<long> candidateIds = await dbContext.Refunds.AsNoTracking()
            .Where(refund => (refund.Status == RefundStatus.AwaitingPartnerReturn || refund.Status == RefundStatus.ReturnReported) &&
                              refund.ReturnDeadlineAtUtc <= now && refund.DebtRecordedAtUtc == null)
            .OrderBy(refund => refund.ReturnDeadlineAtUtc).ThenBy(refund => refund.Id)
            .Select(refund => refund.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        var recordedCount = 0;
        foreach (long refundId in candidateIds)
        {
            bool recorded = await dbContext.ExecuteInSerializableTransactionAsync(
                token => TryRecordDebtAsync(refundId, now, token), cancellationToken);
            if (recorded) recordedCount++;
        }

        return recordedCount;
    }

    private async Task<bool> TryRecordDebtAsync(long refundId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(item => item.Id == refundId, cancellationToken);
        if (refund is null || refund.Status is not (RefundStatus.AwaitingPartnerReturn or RefundStatus.ReturnReported) ||
            refund.DebtRecordedAtUtc is not null || refund.ReturnDeadlineAtUtc is not DateTimeOffset deadline ||
            deadline > now)
        {
            return false;
        }

        Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
            .SingleOrDefaultAsync(item => item.Id == refund.BookingId, cancellationToken);
        if (booking is null) throw new InvalidOperationException("Refund booking no longer exists.");

        // Confirm() only ever sets a return deadline for a non-zero share, so this is always
        // positive here — the invariant RecordOverdueDebtsAsync's own filter already relies on.
        decimal debtAmount = refund.OperatorReturnShare!.Value;
        long operatorUserId = booking.TourSchedule.Tour.OperatorUserId;
        dbContext.TourOperatorDebts.Add(TourOperatorDebt.Create(operatorUserId, debtAmount, now, now));
        refund.RecordOperatorDebt(now);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(null, "RefundOperatorDebtRecorded", "Refund", refund.Id,
            now, AuditOutcome.Success, afterData: JsonSerializer.Serialize(new { DebtAmount = debtAmount, operatorUserId })));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}