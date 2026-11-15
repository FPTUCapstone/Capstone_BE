using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// BR-187 (SRS 3.8.4.3, spec v1.2): a refund request the Tour Operator has not confirmed within
/// 3 working days of its creation is confirmed automatically, the BR-144 return deadline starts,
/// and the Tour Operator is notified (MSG197). Mirrors <see cref="ConfirmBookingRefundCommandHandler"/>'s
/// own BR-139 share calculation; only who confirms (the system, not the Tour Operator) differs.
/// </summary>
public sealed class OperatorConfirmationDeadlineProcessor(
    IApplicationDbContext dbContext, IDateTimeProvider clock, OperatorConfirmationDeadlineOptions options)
{
    private const int DeadlineWorkingDays = 3;

    public async Task<int> AutoConfirmOverdueRefundsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        List<long> candidateIds = await dbContext.Refunds.AsNoTracking()
            .Where(refund => refund.Status == RefundStatus.PendingOperatorConfirmation)
            .OrderBy(refund => refund.RequestedAtUtc).ThenBy(refund => refund.Id)
            .Select(refund => refund.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        var confirmedCount = 0;
        foreach (long refundId in candidateIds)
        {
            bool confirmed = await dbContext.ExecuteInSerializableTransactionAsync(
                token => TryAutoConfirmAsync(refundId, now, token), cancellationToken);
            if (confirmed) confirmedCount++;
        }

        return confirmedCount;
    }

    private async Task<bool> TryAutoConfirmAsync(long refundId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(item => item.Id == refundId, cancellationToken);
        if (refund is null || refund.Status != RefundStatus.PendingOperatorConfirmation) return false;
        if (WorkingDayCalculator.AddWorkingDays(refund.RequestedAtUtc, DeadlineWorkingDays) > now) return false;

        Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
            .SingleOrDefaultAsync(item => item.Id == refund.BookingId, cancellationToken);
        if (booking is null) throw new InvalidOperationException("Refund booking no longer exists.");

        decimal transferredToOperator = await dbContext.PayoutSettlements.AsNoTracking()
            .Where(settlement => settlement.BookingId == booking.Id && settlement.Status == PayoutSettlementStatus.Settled)
            .SumAsync(settlement => (decimal?)settlement.PayoutAmount, cancellationToken) ?? 0m;
        decimal share = RefundShareCalculator.CalculateOperatorReturnShare(refund.Amount, transferredToOperator,
            booking.TotalAmount);
        DateTimeOffset? returnDeadlineAtUtc = share > 0m
            ? WorkingDayCalculator.AddWorkingDays(now, DeadlineWorkingDays)
            : null;

        refund.Confirm(share, returnDeadlineAtUtc, now);

        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(null, "RefundAutoConfirmed", "Refund", refund.Id, now,
            AuditOutcome.Success, afterData: JsonSerializer.Serialize(new
            {
                Status = refund.Status.ToString(),
                OperatorReturnShare = share,
            })));
        dbContext.Notifications.Add(new Notification
        {
            UserId = booking.TourSchedule.Tour.OperatorUserId,
            Channel = NotificationChannel.Push,
            Type = "RefundAutoConfirmed",
            Title = "A refund request was automatically confirmed",
            Body = $"Refund for booking {booking.BookingCode} was automatically confirmed after 3 working days " +
                "without your response.",
            RelatedEntityType = "Refund",
            RelatedEntityId = refund.Id,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}