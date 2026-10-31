using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Cancel;
using TripMate.Application.Features.Bookings.Common;
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

        var decision = BookingCancellationPreviewPolicy.Evaluate(booking, isTraveler, clock.UtcNow);
        if (decision.CanCancel && decision.RefundAmount > 0m
            && await BookingCancellationPaymentVerifier.FindSuccessfulPaymentAsync(dbContext, booking, cancellationToken)
                is null)
        {
            decision = (false, 0m, BookingCancellationErrorCodes.PaymentNotRefundable,
                "This booking has no verified payment eligible for a refund.");
        }
        return Result.Success(new BookingCancellationPreviewResponse(booking.Id, booking.TourSchedule.Tour.Title,
            booking.TourSchedule.StartAtUtc, booking.Quantity, booking.TotalAmount, booking.Status.ToString(),
            booking.PaymentStatus.ToString(), decision.CanCancel, decision.Code, decision.Message,
            decision.RefundAmount, decision.RefundAmount > 0m ? nameof(RefundStatus.Pending) : "NotRequired"));
    }
}

internal static class BookingCancellationPreviewPolicy
{
    internal static (bool CanCancel, decimal RefundAmount, string? Code, string? Message) Evaluate(
        Domain.Entities.Booking booking, bool isTraveler, DateTimeOffset nowUtc)
    {
        if (!BookingCancelEligibility.Evaluate(booking.Status,
                booking.Tickets.Any(ticket => ticket.Status == TicketStatus.Used)).CanCancel)
            return (false, 0m, BookingCancellationErrorCodes.InvalidState, "This booking can no longer be cancelled.");
        if (booking.Status == BookingStatus.PendingPayment) return (true, 0m, null, null);
        if (!isTraveler) return (true, booking.TotalAmount, null, null);
        if (!TravelerCancellationPolicy.TryParse(booking.CancellationPolicySnapshot, out var policy))
            return (false, 0m, BookingCancellationErrorCodes.PolicyUnavailable,
                "The booking cancellation policy is unavailable.");
        var percent = policy!.RefundPercent(nowUtc, booking.TourSchedule!.StartAtUtc);
        if (percent is null)
            return (false, 0m, BookingCancellationErrorCodes.WindowClosed,
                "Bookings cannot be cancelled less than 24 hours before departure.");
        return (true, decimal.Round(booking.TotalAmount * percent.Value / 100m, 2, MidpointRounding.AwayFromZero), null, null);
    }
}