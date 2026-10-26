using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Bookings.Common;
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
        var now = clock.UtcNow;
        var claim = await dbContext.ExecuteInSerializableTransactionAsync(
            token => ClaimPaymentTransactionAsync(bookingId, travelerId, now, token),
            cancellationToken);

        if (!claim.IsSuccess)
        {
            return Result.Failure<CreatePaymentLinkResponse>(
                claim.ErrorCode ?? PaymentErrorCodes.PaymentGatewayUnavailable,
                claim.ErrorMessage ?? "The payment link could not be prepared.");
        }

        if (claim.Value.ExistingResponse is not null)
        {
            return Result.Success(claim.Value.ExistingResponse);
        }

        var transaction = claim.Value.Transaction!;
        var booking = claim.Value.Booking;
        PaymentLinkResult linkResult;
        try
        {
            linkResult = await paymentGateway.CreatePaymentLinkAsync(new PaymentLinkRequest(
                BookingId: booking.Id,
                OrderCode: transaction.OrderCode,
                Amount: booking.TotalAmount,
                Currency: PaymentTransaction.DefaultCurrency,
                Description: $"TM {booking.BookingCode}",
                ExpiresAtUtc: transaction.ExpiresAtUtc,
                ReturnUrl: AppendBookingId(paymentOptions.PayOs.ReturnUrl, booking.Id),
                CancelUrl: AppendBookingId(paymentOptions.PayOs.CancelUrl, booking.Id)), cancellationToken);
        }
        catch (Exception)
        {
            transaction.MarkFailed("Payment gateway link creation failed.", clock.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result.Failure<CreatePaymentLinkResponse>(
                PaymentErrorCodes.PaymentGatewayUnavailable, "The payment gateway is temporarily unavailable.");
        }

        transaction.UpdateCheckoutDetails(
            linkResult.PaymentLinkId,
            linkResult.CheckoutUrl,
            linkResult.QrCode,
            linkResult.ExpiresAtUtc ?? transaction.ExpiresAtUtc);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Never leave a remote link payable when its checkout metadata cannot be persisted.
            try
            {
                await paymentGateway.CancelPaymentLinkAsync(
                    new PaymentLinkCancellationRequest(
                        transaction.OrderCode,
                        linkResult.PaymentLinkId,
                        "TripMate could not persist payment link details"),
                    cancellationToken);
            }
            catch (Exception)
            {
                // The recorded claim lets operational reconciliation locate this attempt.
            }

            dbContext.ClearTrackedEntities();
            var claimedTransaction = await dbContext.PaymentTransactions
                .FirstOrDefaultAsync(t => t.OrderCode == transaction.OrderCode, cancellationToken);
            if (claimedTransaction is not null && claimedTransaction.Status == PaymentTransactionStatus.Pending)
            {
                claimedTransaction.MarkFailed("Payment link details could not be persisted.", clock.UtcNow);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return Result.Failure<CreatePaymentLinkResponse>(
                PaymentErrorCodes.PaymentGatewayUnavailable, "The payment gateway is temporarily unavailable.");
        }

        return Result.Success(MapResponse(transaction, booking));
    }

    private async Task<Result<PaymentLinkClaim>> ClaimPaymentTransactionAsync(
        long bookingId,
        long travelerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var booking = await dbContext.Bookings
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        if (booking is null || booking.TravelerUserId != travelerId)
        {
            return Result.Failure<PaymentLinkClaim>(
                PaymentErrorCodes.PaymentNotFound, "The requested booking was not found.");
        }

        if (booking.PaymentExpiresAtUtc.HasValue && booking.PaymentExpiresAtUtc.Value <= now)
        {
            if (booking.Status == BookingStatus.PendingPayment)
            {
                await BookingExpiryEffects.ExpireAsync(dbContext, booking, now, cancellationToken);
                var activePending = await dbContext.PaymentTransactions
                    .Where(t => t.BookingId == bookingId && t.Status == PaymentTransactionStatus.Pending)
                    .ToListAsync(cancellationToken);
                foreach (var tx in activePending)
                {
                    tx.MarkExpired(null, now);
                }
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return Result.Failure<PaymentLinkClaim>(
                PaymentErrorCodes.PaymentBookingExpired, "The booking payment window has expired.");
        }

        if (booking.Status != BookingStatus.PendingPayment || booking.PaymentStatus == PaymentStatus.Paid)
        {
            return Result.Failure<PaymentLinkClaim>(
                PaymentErrorCodes.PaymentBookingNotEligible, "The booking is not eligible for payment.");
        }

        var existingPending = await dbContext.PaymentTransactions
            .Where(t => t.BookingId == bookingId && t.Status == PaymentTransactionStatus.Pending)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingPending is not null)
        {
            if (existingPending.ExpiresAtUtc.HasValue && existingPending.ExpiresAtUtc.Value <= now)
            {
                existingPending.MarkExpired(null, now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(existingPending.CheckoutUrl))
            {
                return Result.Success(PaymentLinkClaim.Reuse(booking, MapResponse(existingPending, booking)));
            }
            else
            {
                return Result.Failure<PaymentLinkClaim>(
                    PaymentErrorCodes.PaymentGatewayUnavailable,
                    "The payment link is being prepared. Please try again shortly.");
            }
        }

        var linkExpiry = GetLinkExpiry(booking.PaymentExpiresAtUtc, now);
        var transaction = PaymentTransaction.Create(
            bookingId: booking.Id,
            gateway: paymentGateway.Gateway,
            amount: booking.TotalAmount,
            orderCode: GenerateOrderCode(now),
            createdAtUtc: now,
            expiresAtUtc: linkExpiry,
            currency: PaymentTransaction.DefaultCurrency);

        // Persist the Pending claim before calling the external gateway. The filtered unique
        // index and serializable transaction guarantee one active attempt per booking.
        dbContext.PaymentTransactions.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(PaymentLinkClaim.Create(booking, transaction));
    }

    private DateTimeOffset? GetLinkExpiry(DateTimeOffset? bookingExpiry, DateTimeOffset now)
    {
        var linkExpiry = bookingExpiry;
        if (paymentOptions.Mock.ExpiryMinutes > 0)
        {
            var mockExpiry = now.AddMinutes(paymentOptions.Mock.ExpiryMinutes);
            if (!linkExpiry.HasValue || mockExpiry < linkExpiry.Value)
            {
                linkExpiry = mockExpiry;
            }
        }

        return linkExpiry;
    }

    private sealed record PaymentLinkClaim(
        Booking Booking,
        PaymentTransaction? Transaction,
        CreatePaymentLinkResponse? ExistingResponse)
    {
        public static PaymentLinkClaim Create(Booking booking, PaymentTransaction transaction) =>
            new(booking, transaction, null);

        public static PaymentLinkClaim Reuse(Booking booking, CreatePaymentLinkResponse response) =>
            new(booking, null, response);
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
            QrCode: transaction.QrCode,
            PaymentLinkExpiresAtUtc: transaction.ExpiresAtUtc,
            BookingPaymentExpiresAtUtc: booking.PaymentExpiresAtUtc);

    private static long GenerateOrderCode(DateTimeOffset now)
    {
        // Fits well within 15-digit safe integer range
        var timestamp = now.ToUnixTimeMilliseconds();
        return timestamp > 0 ? timestamp : Math.Abs(timestamp) + 1;
    }

    private static string AppendBookingId(string callbackUrl, long bookingId)
    {
        var separator = callbackUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{callbackUrl}{separator}bookingId={bookingId}";
    }
}