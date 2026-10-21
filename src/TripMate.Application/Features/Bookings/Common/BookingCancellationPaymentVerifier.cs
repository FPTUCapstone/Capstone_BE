using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Common;

/// <summary>
/// Defines the single payment-evidence rule used before a cancellation can promise or create a refund.
/// A denormalized booking payment status alone is not sufficient evidence for a refund.
/// </summary>
public static class BookingCancellationPaymentVerifier
{
    public static Task<PaymentTransaction?> FindSuccessfulPaymentAsync(
        IApplicationDbContext dbContext,
        Booking booking,
        CancellationToken cancellationToken) =>
        dbContext.PaymentTransactions
            .Where(transaction => transaction.BookingId == booking.Id
                && transaction.TransactionType == PaymentTransaction.TypePayment
                && transaction.Status == PaymentTransactionStatus.Success
                && transaction.Amount == booking.TotalAmount)
            .OrderByDescending(transaction => transaction.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
}