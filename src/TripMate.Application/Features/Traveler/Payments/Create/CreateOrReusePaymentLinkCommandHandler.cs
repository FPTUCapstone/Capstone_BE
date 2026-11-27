using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    IPaymentCredentialProtector credentialProtector,
    IOperatorPaymentGatewayFactory operatorGatewayFactory,
    PaymentOptions paymentOptions,
    ILogger<CreateOrReusePaymentLinkCommandHandler> logger)
    : IRequestHandler<CreateOrReusePaymentLinkCommand, Result<CreatePaymentLinkResponse>>
{
    private const int MaxAttempts = 3;
    private const int OperatorDirectLinkExpiryMinutes = 15;

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

        // Resolve gateway outside the serializable transaction (read-only channel lookup).
        var gatewayResult = await ResolveGatewayAsync(bookingId, travelerId, now, cancellationToken);
        if (!gatewayResult.IsSuccess)
        {
            return Result.Failure<CreatePaymentLinkResponse>(
                gatewayResult.ErrorCode!, gatewayResult.ErrorMessage!);
        }

        var (resolvedGateway, booking, channel) = gatewayResult.Value;

        var claim = await dbContext.ExecuteInSerializableTransactionAsync(
            token => ClaimPaymentTransactionAsync(booking, channel, resolvedGateway.Gateway, now, token),
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
        PaymentLinkResult linkResult;
        try
        {
            linkResult = await resolvedGateway.CreatePaymentLinkAsync(new PaymentLinkRequest(
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
                await resolvedGateway.CancelPaymentLinkAsync(
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

    private async Task<Result<(IPaymentGateway Gateway, Booking Booking, OperatorPaymentChannel? Channel)>> ResolveGatewayAsync(
        long bookingId,
        long travelerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var booking = await dbContext.Bookings
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        if (booking is null || booking.TravelerUserId != travelerId)
        {
            return Result.Failure<(IPaymentGateway, Booking, OperatorPaymentChannel?)>(
                PaymentErrorCodes.PaymentNotFound, "The requested booking was not found.");
        }

        if (booking.Status != BookingStatus.PendingPayment || booking.PaymentStatus == PaymentStatus.Paid)
        {
            return Result.Failure<(IPaymentGateway, Booking, OperatorPaymentChannel?)>(
                PaymentErrorCodes.PaymentBookingNotEligible, "The booking is not eligible for payment.");
        }

        if (booking.PaymentFlow == PaymentFlow.PlatformCollectedLegacy)
            return Result.Success((paymentGateway, booking, (OperatorPaymentChannel?)null));

        // OperatorDirect: look up operator via TourSchedule → Tour projection, no eager-load.
        var operatorUserId = await dbContext.TourSchedules
            .Where(ts => ts.Id == booking.TourScheduleId)
            .Select(ts => ts.Tour.OperatorUserId)
            .FirstOrDefaultAsync(cancellationToken);

        // OperatorDirect: resolve the TO's Ready channel — no fallback (CR-01).
        var channel = await dbContext.OperatorPaymentChannels
            .FirstOrDefaultAsync(
                c => c.TourOperatorUserId == operatorUserId && c.Status == OperatorPaymentChannel.StatusReady,
                cancellationToken);

        if (channel is null)
        {
            return Result.Failure<(IPaymentGateway, Booking, OperatorPaymentChannel?)>(
                PaymentErrorCodes.ChannelNotReady,
                "The tour operator's payment channel is not ready. Please try again later or contact support.");
        }

        string clientId, apiKey, checksumKey;
        try
        {
            clientId = credentialProtector.Unprotect(channel.ClientIdRef!);
            apiKey = credentialProtector.Unprotect(channel.ApiKeyRef!);
            checksumKey = credentialProtector.Unprotect(channel.ChecksumKeyRef!);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to decrypt credentials for operator channel {ChannelId}", channel.Id);
            return Result.Failure<(IPaymentGateway, Booking, OperatorPaymentChannel?)>(
                PaymentErrorCodes.ChannelNotReady,
                "The tour operator's payment channel is temporarily unavailable.");
        }

        var snapshot = new OperatorChannelSnapshot(
            clientId, apiKey, checksumKey,
            ReturnUrl: paymentOptions.PayOs.ReturnUrl,
            CancelUrl: paymentOptions.PayOs.CancelUrl);

        return Result.Success((operatorGatewayFactory.Create(snapshot), booking, (OperatorPaymentChannel?)channel));
    }

    private async Task<Result<PaymentLinkClaim>> ClaimPaymentTransactionAsync(
        Booking booking,
        OperatorPaymentChannel? channel,
        PaymentGateway gateway,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Expiry is checked inside the tx so its save is atomic with other booking state changes.
        if (booking.PaymentExpiresAtUtc.HasValue && booking.PaymentExpiresAtUtc.Value <= now)
        {
            if (booking.Status == BookingStatus.PendingPayment)
            {
                await BookingExpiryEffects.ExpireAsync(dbContext, booking, now, cancellationToken);
                var activePending = await dbContext.PaymentTransactions
                    .Where(t => t.BookingId == booking.Id && t.Status == PaymentTransactionStatus.Pending)
                    .ToListAsync(cancellationToken);
                foreach (var tx in activePending)
                    tx.MarkExpired(null, now);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return Result.Failure<PaymentLinkClaim>(
                PaymentErrorCodes.PaymentBookingExpired, "The booking payment window has expired.");
        }

        var existingPending = await dbContext.PaymentTransactions
            .Where(t => t.BookingId == booking.Id && t.Status == PaymentTransactionStatus.Pending)
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
                if (booking.PaymentFlow == PaymentFlow.OperatorDirect
                    && (existingPending.OperatorChannelId is null || existingPending.OperatorChannelVersion is not > 0
                        || string.IsNullOrWhiteSpace(existingPending.OperatorChecksumKeyRef)))
                    return Result.Failure<PaymentLinkClaim>(PaymentErrorCodes.PaymentReconciliationRequired,
                        "This older payment link requires reconciliation before another payment can be requested.");
                return Result.Success(PaymentLinkClaim.Reuse(booking, MapResponse(existingPending, booking)));
            }
            else
            {
                return Result.Failure<PaymentLinkClaim>(
                    PaymentErrorCodes.PaymentGatewayUnavailable,
                    "The payment link is being prepared. Please try again shortly.");
            }
        }

        var linkExpiry = GetLinkExpiry(booking, now);
        var transaction = PaymentTransaction.Create(
            bookingId: booking.Id,
            gateway: gateway,
            amount: booking.TotalAmount,
            orderCode: GenerateOrderCode(now),
            createdAtUtc: now,
            expiresAtUtc: linkExpiry,
            currency: PaymentTransaction.DefaultCurrency);

        if (channel is not null)
        {
            if (!await dbContext.OperatorPaymentChannels.AnyAsync(c => c.Id == channel.Id
                && c.ChannelVersion == channel.ChannelVersion && c.Status == OperatorPaymentChannel.StatusReady, cancellationToken))
                return Result.Failure<PaymentLinkClaim>(PaymentErrorCodes.ChannelNotReady, "The channel changed before payment creation.");
            transaction.BindOperatorChannel(channel.Id, channel.ChannelVersion, channel.ChecksumKeyRef!);
        }

        // Persist the Pending claim before calling the external gateway. The filtered unique
        // index and serializable transaction guarantee one active attempt per booking.
        dbContext.PaymentTransactions.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(PaymentLinkClaim.Create(booking, transaction));
    }

    private DateTimeOffset? GetLinkExpiry(Booking booking, DateTimeOffset now)
    {
        var linkExpiry = booking.PaymentExpiresAtUtc;

        if (booking.PaymentFlow == PaymentFlow.OperatorDirect)
        {
            // CR-03: 15-minute real UTC cap for OperatorDirect (not configurable).
            var directCap = now.AddMinutes(OperatorDirectLinkExpiryMinutes);
            if (!linkExpiry.HasValue || directCap < linkExpiry.Value)
                linkExpiry = directCap;
        }
        else if (paymentOptions.Mock.ExpiryMinutes > 0)
        {
            var mockExpiry = now.AddMinutes(paymentOptions.Mock.ExpiryMinutes);
            if (!linkExpiry.HasValue || mockExpiry < linkExpiry.Value)
                linkExpiry = mockExpiry;
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
        var timestamp = now.ToUnixTimeMilliseconds();
        return timestamp > 0 ? timestamp : Math.Abs(timestamp) + 1;
    }

    private static string AppendBookingId(string callbackUrl, long bookingId)
    {
        var separator = callbackUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{callbackUrl}{separator}bookingId={bookingId}";
    }
}