using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Payouts.GetDetails;

public sealed class GetPayoutDetailsQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetPayoutDetailsQuery, Result<PayoutDetailsDto>>
{
    public async Task<Result<PayoutDetailsDto>> Handle(
        GetPayoutDetailsQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Administrator")
        {
            return Result.Failure<PayoutDetailsDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        var payout = await dbContext.Payouts
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.PayoutId)
            .Select(candidate => new PayoutRow(
                candidate.Id,
                candidate.OperatorUserId,
                candidate.Operator.CompanyName,
                candidate.Operator.CommissionRate,
                candidate.PeriodStart,
                candidate.PeriodEnd,
                candidate.GrossRevenue,
                candidate.CommissionAmount,
                candidate.NetAmount,
                candidate.RequestedAtUtc,
                candidate.Status))
            .FirstOrDefaultAsync(cancellationToken);

        if (payout is null)
        {
            // D3: the SRS names locked MSG128 explicitly for a missing selected payout record.
            // No audit row is written because no breakdown was accessed.
            return Result.Failure<PayoutDetailsDto>(
                PayoutErrorCodes.NotFound,
                PayoutErrorCodes.NotFoundMessage);
        }

        // BR-130 (D8): fail-closed — the access audit row is persisted before any financial
        // breakdown is assembled; an audit failure fails the request instead of serving it
        // untracked. This is the single permitted write (PC-02).
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId,
            AuditActionTypes.PayoutDetailsViewed,
            AuditEntityTypes.Payout,
            request.PayoutId,
            dateTimeProvider.UtcNow,
            AuditOutcome.Success));
        await dbContext.SaveChangesAsync(cancellationToken);

        var bookingRows = await dbContext.PayoutItems
            .AsNoTracking()
            .Where(item => item.PayoutId == request.PayoutId)
            .Select(item => new BookingRow(
                item.BookingId,
                item.Booking.BookingCode,
                // LEFT JOIN through the nullable schedule; null title means "no schedule linked".
                item.Booking.TourSchedule.Tour.Title,
                item.Amount))
            .ToListAsync(cancellationToken);

        var bookingIds = bookingRows.Select(row => row.BookingId).ToArray();
        var paidByBooking = await dbContext.PaymentTransactions
            .AsNoTracking()
            .Where(transaction => bookingIds.Contains(transaction.BookingId)
                && transaction.TransactionType == PaymentTransaction.TypePayment
                && transaction.Status == PaymentTransaction.StatusSuccess)
            .GroupBy(transaction => transaction.BookingId)
            .Select(group => new { BookingId = group.Key, Amount = group.Sum(entry => entry.Amount) })
            .ToDictionaryAsync(entry => entry.BookingId, entry => entry.Amount, cancellationToken);
        var refundedByBooking = await dbContext.Refunds
            .AsNoTracking()
            .Where(refund => bookingIds.Contains(refund.BookingId)
                && refund.Status == Refund.StatusProcessed)
            .GroupBy(refund => refund.BookingId)
            .Select(group => new { BookingId = group.Key, Amount = group.Sum(entry => entry.Amount) })
            .ToDictionaryAsync(entry => entry.BookingId, entry => entry.Amount, cancellationToken);

        var bookings = bookingRows
            .OrderBy(row => row.BookingCode, StringComparer.Ordinal)
            .Select(row => new PayoutBookingDto(
                row.BookingId.ToString(CultureInfo.InvariantCulture),
                row.BookingCode,
                row.TourName,
                paidByBooking.GetValueOrDefault(row.BookingId),
                refundedByBooking.GetValueOrDefault(row.BookingId),
                row.Amount))
            .ToList();

        return Result.Success(new PayoutDetailsDto(
            payout.PayoutId.ToString(CultureInfo.InvariantCulture),
            Payout.PayoutCodePrefix + payout.PayoutId.ToString(CultureInfo.InvariantCulture),
            new PayoutOperatorDto(
                payout.OperatorUserId.ToString(CultureInfo.InvariantCulture),
                payout.CompanyName),
            payout.PeriodStart,
            payout.PeriodEnd,
            payout.GrossRevenue,
            payout.CommissionRate,
            payout.CommissionAmount,
            payout.NetAmount,
            payout.RequestedAtUtc,
            payout.Status,
            bookings));
    }

    private sealed record PayoutRow(
        long PayoutId,
        long OperatorUserId,
        string CompanyName,
        decimal CommissionRate,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        decimal GrossRevenue,
        decimal CommissionAmount,
        decimal NetAmount,
        DateTimeOffset? RequestedAtUtc,
        string Status);

    private sealed record BookingRow(
        long BookingId,
        string BookingCode,
        string? TourName,
        decimal Amount);
}