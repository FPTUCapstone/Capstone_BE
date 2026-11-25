using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Preview;

public sealed class GetBookingCancellationPreviewQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetBookingCancellationPreviewQuery, Result<BookingCancellationPreviewResponse>>
{
    public async Task<Result<BookingCancellationPreviewResponse>> Handle(
        GetBookingCancellationPreviewQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long actorUserId || actorUserId <= 0
            || currentUser.Role is not (nameof(UserRole.Traveler) or nameof(UserRole.TourOperator)))
        {
            return Result.Failure<BookingCancellationPreviewResponse>(BookingCancellationErrorCodes.Unauthorized,
                "An authenticated Traveler or Tour Operator account is required.");
        }

        var booking = await dbContext.Bookings.AsNoTracking()
            .Include(candidate => candidate.TourSchedule!).ThenInclude(schedule => schedule.Tour)
            .Include(candidate => candidate.Tickets)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.BookingId, cancellationToken);
        if (booking is null || booking.TourSchedule is null)
        {
            return Result.Failure<BookingCancellationPreviewResponse>(BookingCancellationErrorCodes.NotFound,
                "The booking could not be found.");
        }

        bool isTraveler = currentUser.Role == nameof(UserRole.Traveler);
        if (isTraveler ? booking.TravelerUserId != actorUserId : booking.TourSchedule.Tour.OperatorUserId != actorUserId)
        {
            return Result.Failure<BookingCancellationPreviewResponse>(BookingCancellationErrorCodes.Forbidden,
                "You are not permitted to view this booking cancellation preview.");
        }

        PaymentTransaction? successfulPayment = await BookingCancellationPaymentVerifier
            .FindSuccessfulPaymentAsync(dbContext, booking, cancellationToken);
        var decision = BookingCancellationRefundRules.Evaluate(booking, isTraveler, clock.UtcNow,
            confirmed: false, successfulPayment);

        // UC-41 6.a1/6.b1 (SRS 3.8.4.2): a missing or amount-mismatched payment never blocks the
        // cancellation itself — it only changes the previewed refund outcome.
        bool canCancel = decision.ErrorCode is null
            || decision.ErrorCode == BookingCancellationErrorCodes.ConfirmationRequired;
        string? code = decision.ErrorCode;
        string? message = decision.ErrorMessage;

        decimal refundAmount = 0m;
        string refundStatus = "NotRequired";
        if (canCancel && decision.RefundAmount > 0m)
        {
            if (successfulPayment is not null)
            {
                refundAmount = decision.RefundAmount;
                refundStatus = nameof(RefundStatus.PendingOperatorConfirmation);
            }
            else if (await BookingCancellationPaymentVerifier.FindAnySuccessfulPaymentAsync(dbContext, booking, cancellationToken) is not null)
            {
                refundAmount = decision.RefundAmount;
                refundStatus = nameof(RefundStatus.Reconciliation);
            }
        }

        return Result.Success(new BookingCancellationPreviewResponse(booking.Id, booking.TourSchedule.Tour.Title,
            booking.TourSchedule.StartAtUtc, booking.Quantity, booking.TotalAmount, booking.Status.ToString(),
            booking.PaymentStatus.ToString(), canCancel, code, message,
            refundAmount, refundStatus));
    }
}