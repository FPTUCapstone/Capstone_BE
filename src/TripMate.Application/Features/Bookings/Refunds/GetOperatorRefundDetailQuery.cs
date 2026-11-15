using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>UC-42 read endpoint for FE (Q-FE2, spec v1.2 §3.3): a single refund's detail for the
/// Tour Operator that owns its booking's tour.</summary>
public sealed record GetOperatorRefundDetailQuery(long RefundId) : IRequest<Result<OperatorRefundDetailDto>>;

/// <summary><see cref="OperatorReturnShare"/> is the confirmed BR-139 share, or — while the refund
/// still awaits confirmation — the share a confirmation right now would fix (SRS 3.8.4.3 step 2).
/// <see cref="ReferenceCode"/> is the transfer description the Tour Operator is asked to use.</summary>
public sealed record OperatorRefundDetailDto(long RefundId, long BookingId, string BookingCode, string TourName,
    DateOnly DepartureDate, decimal PaidAmount, string ReferenceCode, decimal RefundAmount,
    string Status, decimal OperatorReturnShare, DateTimeOffset? OperatorConfirmedAtUtc,
    DateTimeOffset? ReturnDeadlineAtUtc, string? ReturnReference, DateTimeOffset? ReturnReportedAtUtc,
    DateTimeOffset? ReturnReceivedAtUtc, DateTimeOffset RequestedAtUtc)
{
    public const string ReferenceCodePrefix = "TM-REFUND-";
}

public sealed class GetOperatorRefundDetailQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetOperatorRefundDetailQuery, Result<OperatorRefundDetailDto>>
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public async Task<Result<OperatorRefundDetailDto>> Handle(GetOperatorRefundDetailQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<OperatorRefundDetailDto>(BookingCancellationErrorCodes.Unauthorized,
                "Tour Operator access is required to view this refund.");

        Refund? refund = await dbContext.Refunds.AsNoTracking()
            .Include(item => item.Booking).ThenInclude(booking => booking.TourSchedule).ThenInclude(schedule => schedule.Tour)
            .SingleOrDefaultAsync(item => item.Id == request.RefundId, cancellationToken);
        if (refund is null)
            return Result.Failure<OperatorRefundDetailDto>(BookingCancellationErrorCodes.NotFound,
                "The refund could not be found.");
        if (refund.Booking.TourSchedule.Tour.OperatorUserId != operatorUserId)
            return Result.Failure<OperatorRefundDetailDto>(RefundConfirmationErrorCodes.OperatorNotOwner,
                "Only the Tour Operator of this booking's tour may view this refund.");

        Booking booking = refund.Booking;
        decimal share = refund.OperatorReturnShare ?? RefundShareCalculator.CalculateOperatorReturnShare(
            refund.Amount,
            await dbContext.PayoutSettlements.AsNoTracking()
                .Where(settlement => settlement.BookingId == booking.Id && settlement.Status == PayoutSettlementStatus.Settled)
                .SumAsync(settlement => (decimal?)settlement.PayoutAmount, cancellationToken) ?? 0m,
            booking.TotalAmount);

        return Result.Success(new OperatorRefundDetailDto(refund.Id, refund.BookingId, booking.BookingCode,
            booking.TourSchedule.Tour.Title,
            DateOnly.FromDateTime(booking.TourSchedule.StartAtUtc.ToOffset(VietnamOffset).DateTime), booking.TotalAmount,
            $"{OperatorRefundDetailDto.ReferenceCodePrefix}{refund.Id}",
            refund.Amount, refund.Status.ToString(), share, refund.OperatorConfirmedAtUtc,
            refund.ReturnDeadlineAtUtc, refund.ReturnReference, refund.ReturnReportedAtUtc, refund.ReturnReceivedAtUtc,
            refund.RequestedAtUtc));
    }
}