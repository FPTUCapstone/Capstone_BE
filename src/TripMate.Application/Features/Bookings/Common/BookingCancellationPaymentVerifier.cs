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

    /// <summary>UC-41 6.b1 (SRS 3.8.4.2): a successful payment exists but its verified amount does
    /// not match the booking's paid amount — <see cref="FindSuccessfulPaymentAsync"/> already
    /// filters by matching amount, so it alone cannot distinguish "no payment at all" (6.a1) from
    /// "payment exists but mismatched" (6.b1). Call this only after the amount-matched lookup
    /// above has already returned null, to tell the two cases apart.</summary>
    public static Task<PaymentTransaction?> FindAnySuccessfulPaymentAsync(
        IApplicationDbContext dbContext,
        Booking booking,
        CancellationToken cancellationToken) =>
        dbContext.PaymentTransactions
            .Where(transaction => transaction.BookingId == booking.Id
                && transaction.TransactionType == PaymentTransaction.TypePayment
                && transaction.Status == PaymentTransactionStatus.Success)
            .OrderByDescending(transaction => transaction.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
}