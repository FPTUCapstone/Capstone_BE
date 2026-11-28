using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Webhook;

public record ProcessOperatorPayOsWebhookCommand(
    long ChannelId,
    PayOsWebhookPayload Payload,
    string? RawBody = null) : IRequest<Result<bool>>;

public sealed class ProcessOperatorPayOsWebhookCommandHandler(
    IApplicationDbContext dbContext,
    IPaymentCredentialProtector credentialProtector,
    IPaymentCompletionProcessor completionProcessor)
    : IRequestHandler<ProcessOperatorPayOsWebhookCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(
        ProcessOperatorPayOsWebhookCommand request,
        CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        if (payload?.Data is null || string.IsNullOrWhiteSpace(payload.Signature))
        {
            return Result.Failure<bool>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Invalid PayOS webhook payload structure.");
        }

        if (CommissionPaymentAttempt.IsCommissionOrderCode(payload.Data.OrderCode))
            return Invalid("Commission payments require the platform webhook.");
        var transaction = await dbContext.PaymentTransactions.AsNoTracking().Include(t => t.Booking)
            .FirstOrDefaultAsync(t => t.OrderCode == payload.Data.OrderCode, cancellationToken);
        string? protectedKey;
        if (transaction is not null)
        {
            if (transaction.Booking.PaymentFlow != PaymentFlow.OperatorDirect
                || transaction.OperatorChannelId != request.ChannelId
                || transaction.OperatorChannelVersion is not > 0 || string.IsNullOrWhiteSpace(transaction.OperatorChecksumKeyRef))
                return Invalid("The payment does not belong to this operator channel.");
            protectedKey = transaction.OperatorChecksumKeyRef;
        }
        else
        {
            // payOS confirms a URL before Admin approval. A signed unknown-order sample is
            // acknowledged only; it must never create a transaction or enter completion.
            var channel = await dbContext.OperatorPaymentChannels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == request.ChannelId, cancellationToken);
            if (channel is null || channel.Status == OperatorPaymentChannel.StatusDisabled)
                return Invalid("Payment channel is not available for verification.");
            protectedKey = channel.ChecksumKeyRef;
        }

        string checksumKey;
        try
        {
            checksumKey = credentialProtector.Unprotect(protectedKey!);
        }

        catch (Exception)
        {
            return Result.Failure<bool>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Could not verify webhook signature.");
        }

        if (string.IsNullOrWhiteSpace(checksumKey)) return Invalid("Webhook verification key is missing.");
        if (!ProcessPayOsWebhookCommandHandler.VerifyPayOsSignature(
                payload.Data, payload.Signature, checksumKey))
        {
            return Result.Failure<bool>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Invalid PayOS signature.");
        }

        if (transaction is null) return Result.Success(true);
        if (transaction.Amount != payload.Data.Amount || string.IsNullOrWhiteSpace(payload.Data.Currency)
            || !string.Equals(transaction.Currency, payload.Data.Currency, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(transaction.PaymentLinkId)
            || !string.Equals(transaction.PaymentLinkId, payload.Data.PaymentLinkId, StringComparison.Ordinal))
            return Invalid("The payment notification does not match its saved payment link.");
        if (payload.Code != "00" || payload.Data.Code != "00")
            return Invalid("Only a successful payment notification can complete this payment.");

        var rawResponse = !string.IsNullOrWhiteSpace(request.RawBody)
            ? request.RawBody
            : JsonSerializer.Serialize(payload);

        var completionResult = await completionProcessor.CompleteAsync(
            transactionId: transaction.Id,
            orderCode: payload.Data.OrderCode,
            outcome: PaymentTransactionStatus.Success,
            gatewayRef: payload.Data.Reference ?? payload.Data.PaymentLinkId,
            rawResponse: rawResponse,
            verifiedAmount: payload.Data.Amount,
            verifiedCurrency: payload.Data.Currency,
            cancellationToken: cancellationToken);

        if (!completionResult.IsSuccess)
        {
            return Result.Failure<bool>(
                completionResult.ErrorCode ?? PaymentErrorCodes.PaymentCallbackInvalid,
                completionResult.ErrorMessage ?? "Payment completion failed.");
        }

        return Result.Success(true);
    }

    private static Result<bool> Invalid(string message) => Result.Failure<bool>(PaymentErrorCodes.PaymentCallbackInvalid, message);
}