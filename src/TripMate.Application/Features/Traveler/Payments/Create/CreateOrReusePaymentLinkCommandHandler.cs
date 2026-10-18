using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Create;

public sealed class CreateOrReusePaymentLinkCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    IPaymentGateway paymentGateway,
    PaymentOptions paymentOptions)
    : IRequestHandler<CreateOrReusePaymentLinkCommand, Result<CreatePaymentLinkResponse>>
{
    private const int MaxAttempts = 3;

    public async Task<Result<CreatePaymentLinkResponse>> Handle(
        CreateOrReusePaymentLinkCommand request,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not long travelerId || currentUser.Role != nameof(UserRole.Traveler))
        {
            return Result.Failure<CreatePaymentLinkResponse>(
                PaymentErrorCodes.AccessDenied, "Traveler authorization is required.");
        }

        if (request.BookingId <= 0)
        {
            return Result.Failure<CreatePaymentLinkResponse>(
                PaymentErrorCodes.PaymentNotFound, "Booking was not found.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CreateOrReuseAttemptAsync(request.BookingId, travelerId, cancellationToken);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                dbContext.ClearTrackedEntities();
            }
        }
    }

    private async Task<Result<CreatePaymentLinkResponse>> CreateOrReuseAttemptAsync(
        long bookingId,
        long travelerId,
        CancellationToken cancellationToken)
    {
        var booking = await dbContext.Bookings
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        if (booking is null || booking.TravelerUserId != travelerId)
        {
            return Result.Failure<CreatePaymentLinkResponse>(
                PaymentErrorCodes.PaymentNotFound, "The requested booking was not found.");
        }

        var now = clock.UtcNow;

        // Check if booking payment hold has expired
        if (booking.PaymentExpiresAtUtc.HasValue && booking.PaymentExpiresAtUtc.Value <= now)
        {
            if (booking.Status == BookingStatus.PendingPayment)
            {
                booking.ExpirePendingPayment(now);
                var activePending = await dbContext.PaymentTransactions
                    .Where(t => t.BookingId == bookingId && t.Status == PaymentTransactionStatus.Pending)
                    .ToListAsync(cancellationToken);
                foreach (var tx in activePending)
                {
                    tx.MarkExpired(null, now);
                }
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return Result.Failure<CreatePaymentLinkResponse>(
                PaymentErrorCodes.PaymentBookingExpired, "The booking payment window has expired.");
        }

        // Check if booking is eligible for payment
        if (booking.Status != BookingStatus.PendingPayment || booking.PaymentStatus == PaymentStatus.Paid)
        {
            return Result.Failure<CreatePaymentLinkResponse>(
                PaymentErrorCodes.PaymentBookingNotEligible, "The booking is not eligible for payment.");
        }

        // Check for active pending payment transaction
        var existingPending = await dbContext.PaymentTransactions
            .Where(t => t.BookingId == bookingId && t.Status == PaymentTransactionStatus.Pending)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingPending is not null)
        {
            // If the pending link has expired, mark it Expired so we can issue a replacement
            if (existingPending.ExpiresAtUtc.HasValue && existingPending.ExpiresAtUtc.Value <= now)
            {
                existingPending.MarkExpired(null, now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                // Reuse active link
                return Result.Success(MapResponse(existingPending, booking));
            }
        }

        // Generate unique order code (positive 64-bit integer)
        var orderCode = GenerateOrderCode(now);

        // Compute payment link expiry: minimum of booking expiry and gateway default
        var linkExpiry = booking.PaymentExpiresAtUtc;
        if (paymentOptions.Mock.ExpiryMinutes > 0)
        {
            var mockExpiry = now.AddMinutes(paymentOptions.Mock.ExpiryMinutes);
            if (!linkExpiry.HasValue || mockExpiry < linkExpiry.Value)
            {
                linkExpiry = mockExpiry;
            }
        }

        var linkResult = await paymentGateway.CreatePaymentLinkAsync(new PaymentLinkRequest(
            BookingId: booking.Id,
            OrderCode: orderCode,
            Amount: booking.TotalAmount,
            Currency: PaymentTransaction.DefaultCurrency,
            Description: $"Payment for {booking.BookingCode}",
            ExpiresAtUtc: linkExpiry,
            ReturnUrl: paymentOptions.PayOs.ReturnUrl,
            CancelUrl: paymentOptions.PayOs.CancelUrl), cancellationToken);

        var transaction = PaymentTransaction.Create(
            bookingId: booking.Id,
            gateway: paymentGateway.Gateway,
            amount: booking.TotalAmount,
            orderCode: orderCode,
            createdAtUtc: now,
            expiresAtUtc: linkResult.ExpiresAtUtc ?? linkExpiry,
            checkoutUrl: linkResult.CheckoutUrl,
            paymentLinkId: linkResult.PaymentLinkId,
            currency: PaymentTransaction.DefaultCurrency);

        dbContext.PaymentTransactions.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success(MapResponse(transaction, booking));
    }

    private static CreatePaymentLinkResponse MapResponse(PaymentTransaction transaction, Booking booking) =>
        new(
            PaymentId: transaction.Id,
            BookingId: transaction.BookingId,
            Gateway: transaction.Gateway.ToString(),
            CheckoutUrl: transaction.CheckoutUrl ?? string.Empty,
            Status: transaction.Status.ToString(),
            Amount: transaction.Amount,
            Currency: transaction.Currency,
            PaymentLinkExpiresAtUtc: transaction.ExpiresAtUtc,
            BookingPaymentExpiresAtUtc: booking.PaymentExpiresAtUtc);

    private static long GenerateOrderCode(DateTimeOffset now)
    {
        // Fits well within 15-digit safe integer range
        var timestamp = now.ToUnixTimeMilliseconds();
        return timestamp > 0 ? timestamp : Math.Abs(timestamp) + 1;
    }
}