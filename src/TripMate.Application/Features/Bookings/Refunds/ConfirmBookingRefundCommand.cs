using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// UC-42 step 3 (SRS 3.8.4.3, spec v1.2): the Tour Operator confirms a refund UC-41 created in
/// <see cref="RefundStatus.PendingOperatorConfirmation"/>. Computes the BR-139 operator return
/// share from the booking's Settled payout settlements and moves the refund onward: straight to
/// <see cref="RefundStatus.Pending"/> when the share is zero (4.b1), otherwise to
/// <see cref="RefundStatus.AwaitingPartnerReturn"/> with a 3-working-day return deadline (BR-144).
/// </summary>
public sealed record ConfirmBookingRefundCommand(long RefundId) : IRequest<Result<ConfirmBookingRefundResponse>>;

public sealed record ConfirmBookingRefundResponse(long RefundId, string Status, decimal OperatorReturnShare,
    DateTimeOffset? ReturnDeadlineAtUtc);

public sealed class ConfirmBookingRefundCommandHandler(
    IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<ConfirmBookingRefundCommand, Result<ConfirmBookingRefundResponse>>
{
    private const int ReturnDeadlineWorkingDays = 3;

    public async Task<Result<ConfirmBookingRefundResponse>> Handle(ConfirmBookingRefundCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long operatorUserId || currentUser.Role != nameof(UserRole.TourOperator))
            return Result.Failure<ConfirmBookingRefundResponse>(BookingCancellationErrorCodes.Unauthorized,
                "Tour Operator access is required to confirm a refund.");
        if (request.RefundId <= 0)
            return Result.Failure<ConfirmBookingRefundResponse>(BookingCancellationErrorCodes.NotFound,
                "The refund could not be found.");

        return await dbContext.ExecuteInSerializableTransactionAsync(async token =>
        {
            Refund? refund = await dbContext.Refunds.SingleOrDefaultAsync(item => item.Id == request.RefundId, token);
            if (refund is null)
                return Result.Failure<ConfirmBookingRefundResponse>(BookingCancellationErrorCodes.NotFound,
                    "The refund could not be found.");

            Booking? booking = await dbContext.Bookings.Include(item => item.TourSchedule).ThenInclude(item => item.Tour)
                .SingleOrDefaultAsync(item => item.Id == refund.BookingId, token);
            if (booking is null)
                return Result.Failure<ConfirmBookingRefundResponse>(BookingCancellationErrorCodes.NotFound,
                    "The refund could not be found.");
            if (booking.TourSchedule.Tour.OperatorUserId != operatorUserId)
                return Result.Failure<ConfirmBookingRefundResponse>(RefundConfirmationErrorCodes.OperatorNotOwner,
                    "Only the Tour Operator of this booking's tour may confirm its refund.");
            if (refund.Status != RefundStatus.PendingOperatorConfirmation)
                return Result.Failure<ConfirmBookingRefundResponse>(RefundConfirmationErrorCodes.RequestNotPending,
                    "This refund is not awaiting Tour Operator confirmation.");

            DateTimeOffset now = clock.UtcNow;
            decimal transferredToOperator = await dbContext.PayoutSettlements.AsNoTracking()
                .Where(settlement => settlement.BookingId == booking.Id && settlement.Status == PayoutSettlementStatus.Settled)
                .SumAsync(settlement => (decimal?)settlement.PayoutAmount, token) ?? 0m;
            decimal share = RefundShareCalculator.CalculateOperatorReturnShare(refund.Amount, transferredToOperator,
                booking.TotalAmount);
            DateTimeOffset? returnDeadlineAtUtc = share > 0m
                ? WorkingDayCalculator.AddWorkingDays(now, ReturnDeadlineWorkingDays)
                : null;

            RefundStatus statusBefore = refund.Status;
            refund.Confirm(share, returnDeadlineAtUtc, now);

            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorUserId, "RefundConfirmedByOperator", "Refund",
                refund.Id, now, AuditOutcome.Success,
                beforeData: JsonSerializer.Serialize(new { Status = statusBefore.ToString() }),
                afterData: JsonSerializer.Serialize(new
                {
                    Status = refund.Status.ToString(),
                    OperatorReturnShare = share,
                    refund.ReturnDeadlineAtUtc,
                })));
            await dbContext.SaveChangesAsync(token);

            return Result.Success(new ConfirmBookingRefundResponse(refund.Id, refund.Status.ToString(), share,
                refund.ReturnDeadlineAtUtc));
        }, cancellationToken);
    }
}