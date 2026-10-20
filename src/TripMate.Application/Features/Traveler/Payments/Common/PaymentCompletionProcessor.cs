using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Common;

public sealed class PaymentCompletionProcessor(
    IApplicationDbContext dbContext,
    IDateTimeProvider clock) : IPaymentCompletionProcessor
{
    public async Task<Result<PaymentCompletionResult>> CompleteAsync(
        long? transactionId,
        long? orderCode,
        PaymentTransactionStatus outcome,
        string? gatewayRef,
        string? rawResponse,
        decimal? verifiedAmount,
        string? verifiedCurrency,
        CancellationToken cancellationToken)
    {
        if (transactionId is not > 0 && orderCode is not > 0)
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentNotFound, "Payment transaction identification is required.");
        }

        PaymentTransaction? transaction = null;
        if (transactionId is > 0)
        {
            transaction = await dbContext.PaymentTransactions
                .Include(t => t.Booking)
                .FirstOrDefaultAsync(t => t.Id == transactionId.Value, cancellationToken);
        }
        else if (orderCode is > 0)
        {
            transaction = await dbContext.PaymentTransactions
                .Include(t => t.Booking)
                .FirstOrDefaultAsync(t => t.OrderCode == orderCode.Value, cancellationToken);
        }

        if (transaction is null)
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentNotFound, "Payment transaction was not found.");
        }

        var booking = transaction.Booking
            ?? await dbContext.Bookings.FirstOrDefaultAsync(b => b.Id == transaction.BookingId, cancellationToken);

        if (booking is null)
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentNotFound, "Booking associated with transaction was not found.");
        }

        if (verifiedAmount.HasValue && verifiedAmount.Value != transaction.Amount)
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Gateway amount does not match transaction amount.");
        }

        if (!string.IsNullOrWhiteSpace(verifiedCurrency) &&
            !string.Equals(verifiedCurrency.Trim(), transaction.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<PaymentCompletionResult>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Gateway currency does not match transaction currency.");
        }

        var now = clock.UtcNow;
        var statusChanged = false;
        var acceptedPayment = false;

        switch (outcome)
        {
            case PaymentTransactionStatus.Success:
                if (transaction.Status == PaymentTransactionStatus.Pending)
                {
                    var commissionRate = await dbContext.TourSchedules.Where(s => s.Id == booking.TourScheduleId).Select(s => dbContext.OperatorProfiles.Where(o => o.UserId == s.Tour.OperatorUserId).Select(o => o.CommissionRate).FirstOrDefault()).FirstOrDefaultAsync(cancellationToken);
                    transaction.MarkSuccess(gatewayRef, rawResponse, now, commissionRate);
                    statusChanged = true;
                    acceptedPayment = true;
                }
                else if (transaction.Status != PaymentTransactionStatus.Success)
                {
                    // A terminal transaction must never be reopened by a late PayOS webhook.
                    // Retain its gateway evidence for reconciliation/refund processing instead.
                    transaction.RecordLateGatewayNotification(gatewayRef, rawResponse, now);
                }

                // Decision 6 & 8: If booking is still eligible (PendingPayment and not expired), confirm it.
                // If booking has already expired, keep booking Expired and do not reinstate.
                if (acceptedPayment && booking.Status == BookingStatus.PendingPayment)
                {
                    if (booking.PaymentExpiresAtUtc.HasValue && booking.PaymentExpiresAtUtc.Value < now)
                    {
                        booking.ExpirePendingPayment(now);
                    }
                    else
                    {
                        booking.MarkPaid(now);
                        statusChanged = true;
                    }
                }
                break;

            case PaymentTransactionStatus.Cancelled:
                if (transaction.Status == PaymentTransactionStatus.Pending)
                {
                    transaction.MarkCancelled(rawResponse, now);
                    statusChanged = true;
                }
                // Decision 2: Booking remains PendingPayment until its normal expiry
                break;

            case PaymentTransactionStatus.Expired:
                if (transaction.Status == PaymentTransactionStatus.Pending)
                {
                    transaction.MarkExpired(rawResponse, now);
                    statusChanged = true;
                }
                // Check if booking hold has expired too
                if (booking.Status == BookingStatus.PendingPayment &&
                    booking.PaymentExpiresAtUtc.HasValue &&
                    booking.PaymentExpiresAtUtc.Value <= now)
                {
                    booking.ExpirePendingPayment(now);
                    statusChanged = true;
                }
                break;

            case PaymentTransactionStatus.Failed:
                if (transaction.Status == PaymentTransactionStatus.Pending)
                {
                    transaction.MarkFailed(rawResponse, now);
                    statusChanged = true;
                }
                break;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(new PaymentCompletionResult(transaction, booking, statusChanged));
    }
}