using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Bookings.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Refunds;

public sealed record GetBookingRefundProgressQuery(long BookingId)
    : IRequest<Result<BookingRefundProgressResponse>>;

public sealed record BookingRefundProgressResponse(
    long BookingId,
    string BookingCode,
    decimal RefundAmount,
    string Status,
    string? GatewayReference,
    int AttemptCount,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ProcessedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    string? LastErrorCode);

public sealed class GetBookingRefundProgressQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetBookingRefundProgressQuery, Result<BookingRefundProgressResponse>>
{
    public async Task<Result<BookingRefundProgressResponse>> Handle(GetBookingRefundProgressQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long userId || userId <= 0 || currentUser.Role is not (nameof(UserRole.Traveler) or nameof(UserRole.TourOperator)))
            return Result.Failure<BookingRefundProgressResponse>(BookingCancellationErrorCodes.Unauthorized,
                "An authenticated Traveler or Tour Operator account is required.");

        var result = await dbContext.Refunds.AsNoTracking()
            .Where(refund => refund.BookingId == request.BookingId)
            .OrderByDescending(refund => refund.RequestedAtUtc)
            .Select(refund => new
            {
                Refund = refund,
                refund.Booking.BookingCode,
                refund.Booking.TravelerUserId,
                OperatorUserId = refund.Booking.TourSchedule.Tour.OperatorUserId,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (result is null)
            return Result.Failure<BookingRefundProgressResponse>(BookingCancellationErrorCodes.NotFound,
                "The refund could not be found.");
        if ((currentUser.Role == nameof(UserRole.Traveler) && result.TravelerUserId != userId) ||
            (currentUser.Role == nameof(UserRole.TourOperator) && result.OperatorUserId != userId))
            return Result.Failure<BookingRefundProgressResponse>(BookingCancellationErrorCodes.Forbidden,
                "You are not permitted to view this refund.");

        var refund = result.Refund;
        return Result.Success(new BookingRefundProgressResponse(refund.BookingId, result.BookingCode, refund.Amount,
            refund.Status.ToString(), refund.GatewayReference, refund.AttemptCount, refund.RequestedAtUtc,
            refund.ProcessedAtUtc, refund.NextAttemptAtUtc, refund.LastErrorCode));
    }
}