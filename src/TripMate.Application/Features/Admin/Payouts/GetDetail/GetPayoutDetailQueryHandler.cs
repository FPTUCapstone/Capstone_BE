using System.Globalization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Payouts.GetDetail;

public sealed class GetPayoutDetailQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUserService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<GetPayoutDetailQuery, Result<PayoutDetailDto>>
{
    public async Task<Result<PayoutDetailDto>> Handle(
        GetPayoutDetailQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is null || currentUserService.Role != "Staff")
        {
            return Result.Failure<PayoutDetailDto>(
                PayoutErrorCodes.Forbidden,
                "You do not have permission to access this function.");
        }

        var payout = await dbContext.Payouts
            .AsNoTracking()
            .Where(candidate => candidate.Id == request.PayoutId)
            .Select(candidate => new DetailRow(
                candidate.Id,
                candidate.OperatorUserId,
                candidate.Operator.CompanyName,
                candidate.PayoutCode,
                candidate.PeriodStart,
                candidate.PeriodEnd,
                candidate.GrossRevenue,
                candidate.CommissionAmount,
                candidate.NetAmount,
                candidate.RequestedAtUtc,
                candidate.Status,
                candidate.RejectionReason,
                candidate.BankName,
                candidate.AccountHolderName,
                candidate.AccountNumberMasked))
            .FirstOrDefaultAsync(cancellationToken);

        if (payout is null)
        {
            // No audit row: BR-130 covers access to an existing record's beneficiary data, not a
            // lookup of a nonexistent one.
            return Result.Failure<PayoutDetailDto>(
                PayoutErrorCodes.NotFound,
                PayoutErrorCodes.NotFoundMessage);
        }

        var payoutItems = await dbContext.PayoutItems
            .AsNoTracking()
            .Where(item => item.PayoutId == payout.PayoutId)
            .Select(item => new { item.BookingId, item.PaidAmount })
            .ToListAsync(cancellationToken);
        var paidAmountByBooking = payoutItems.ToDictionary(item => item.BookingId, item => item.PaidAmount);
        var bookingIds = payoutItems.Select(item => item.BookingId).ToList();

        var bookingInfo = await dbContext.Bookings
            .AsNoTracking()
            .Where(booking => bookingIds.Contains(booking.Id))
            .Select(booking => new
            {
                booking.Id,
                booking.BookingCode,
                TourName = booking.TourSchedule.Tour.Title,
                CompletedAtUtc = booking.TourSchedule.EndAtUtc,
            })
            .ToListAsync(cancellationToken);
        var bookingRows = bookingInfo
            .Select(booking => new BookingRow(
                booking.Id, booking.BookingCode, booking.TourName, booking.CompletedAtUtc,
                paidAmountByBooking[booking.Id]))
            .ToList();

        var refundedByBooking = await dbContext.PayoutRefundItems
            .AsNoTracking()
            .Where(item => item.PayoutId == payout.PayoutId)
            .GroupBy(item => item.BookingId)
            .Select(group => new { BookingId = group.Key, Refunded = group.Sum(item => item.RefundedAmount) })
            .ToDictionaryAsync(row => row.BookingId, row => row.Refunded, cancellationToken);

        // BR-130: fail-closed — the access audit row is persisted before the beneficiary data is
        // returned to the caller, same pattern as UC-59's GetActiveTripDetailsQueryHandler.
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
            currentUserService.UserId,
            AuditActionTypes.PayoutDetailsViewed,
            AuditEntityTypes.Payout,
            payout.PayoutId,
            dateTimeProvider.UtcNow,
            AuditOutcome.Success));
        await dbContext.SaveChangesAsync(cancellationToken);

        var commissionRate = payout.GrossRevenue == 0m
            ? 0m
            : payout.CommissionAmount / payout.GrossRevenue;

        var contributingBookings = bookingRows
            .Select(row =>
            {
                var refunded = refundedByBooking.GetValueOrDefault(row.BookingId, 0m);
                return new PayoutContributingBookingDto(
                    row.BookingId.ToString(CultureInfo.InvariantCulture),
                    row.BookingCode,
                    row.TourName,
                    row.CompletedAtUtc,
                    row.PaidAmount,
                    refunded,
                    row.PaidAmount - refunded);
            })
            .OrderBy(dto => dto.CompletedAtUtc)
            .ToList();

        return Result.Success(new PayoutDetailDto(
            payout.PayoutId.ToString(CultureInfo.InvariantCulture),
            payout.PayoutCode,
            payout.Status,
            new PayoutSettlementPeriodDto(payout.PeriodStart, payout.PeriodEnd),
            new PayoutDetailOperatorDto(
                payout.OperatorUserId.ToString(CultureInfo.InvariantCulture),
                payout.CompanyName),
            payout.GrossRevenue,
            commissionRate,
            payout.CommissionAmount,
            payout.NetAmount,
            payout.RequestedAtUtc,
            contributingBookings,
            new PayoutBeneficiaryDto(
                payout.BankName,
                payout.AccountHolderName,
                payout.AccountNumberMasked),
            payout.RejectionReason));
    }

    private sealed record DetailRow(
        long PayoutId,
        long OperatorUserId,
        string CompanyName,
        string PayoutCode,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        decimal GrossRevenue,
        decimal CommissionAmount,
        decimal NetAmount,
        DateTimeOffset? RequestedAtUtc,
        string Status,
        string? RejectionReason,
        string? BankName,
        string? AccountHolderName,
        string? AccountNumberMasked);

    private sealed record BookingRow(
        long BookingId,
        string BookingCode,
        string TourName,
        DateTimeOffset CompletedAtUtc,
        decimal PaidAmount);
}