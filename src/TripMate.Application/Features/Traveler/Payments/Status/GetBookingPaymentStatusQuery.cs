using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Status;

public record PaymentStatusResponse(
    long? PaymentId,
    long BookingId,
    string BookingStatus,
    string PaymentStatus,
    string? Gateway,
    string? TransactionStatus,
    decimal Amount,
    string Currency,
    string? CheckoutUrl,
    DateTimeOffset? PaymentLinkExpiresAtUtc,
    DateTimeOffset? BookingPaymentExpiresAtUtc);

public record GetBookingPaymentStatusQuery(long BookingId) : IRequest<Result<PaymentStatusResponse>>;

public sealed class GetBookingPaymentStatusQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<GetBookingPaymentStatusQuery, Result<PaymentStatusResponse>>
{
    public async Task<Result<PaymentStatusResponse>> Handle(
        GetBookingPaymentStatusQuery request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
        {
            return Result.Failure<PaymentStatusResponse>(
                PaymentErrorCodes.AccessDenied, "Traveler authorization is required.");
        }

        if (request.BookingId <= 0)
        {
            return Result.Failure<PaymentStatusResponse>(
                PaymentErrorCodes.PaymentNotFound, "Booking was not found.");
        }

        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            var booking = await dbContext.Bookings
                .FirstOrDefaultAsync(b => b.Id == request.BookingId, transactionCancellationToken);

            if (booking is null || booking.TravelerUserId != travelerId)
            {
                return Result.Failure<PaymentStatusResponse>(
                    PaymentErrorCodes.PaymentNotFound, "The requested booking was not found.");
            }

            var now = clock.UtcNow;

            // Auto-expire booking if hold window passed
            if (booking.Status == BookingStatus.PendingPayment &&
                booking.PaymentExpiresAtUtc.HasValue &&
                booking.PaymentExpiresAtUtc.Value <= now)
            {
                await BookingExpiryEffects.ExpireAsync(dbContext, booking, now, transactionCancellationToken);
                var activePending = await dbContext.PaymentTransactions
                    .Where(t => t.BookingId == booking.Id && t.Status == PaymentTransactionStatus.Pending)
                    .ToListAsync(transactionCancellationToken);
                foreach (var tx in activePending)
                {
                    tx.MarkExpired(null, now);
                }
                await dbContext.SaveChangesAsync(transactionCancellationToken);
            }

            var latestTransaction = await dbContext.PaymentTransactions
                .Where(t => t.BookingId == booking.Id)
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(transactionCancellationToken);

            return Result.Success(new PaymentStatusResponse(
                PaymentId: latestTransaction?.Id,
                BookingId: booking.Id,
                BookingStatus: booking.Status.ToString(),
                PaymentStatus: booking.PaymentStatus.ToString(),
                Gateway: latestTransaction?.Gateway.ToString(),
                TransactionStatus: latestTransaction?.Status.ToString(),
                Amount: latestTransaction?.Amount ?? booking.TotalAmount,
                Currency: latestTransaction?.Currency ?? PaymentTransaction.DefaultCurrency,
                CheckoutUrl: latestTransaction?.CheckoutUrl,
                PaymentLinkExpiresAtUtc: latestTransaction?.ExpiresAtUtc,
                BookingPaymentExpiresAtUtc: booking.PaymentExpiresAtUtc));
        }, cancellationToken);
    }
}