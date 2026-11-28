using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.CommissionStatements;
using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Payments.Webhook;

public record PayOsWebhookData(
    [property: JsonPropertyName("orderCode")] long OrderCode,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("accountNumber")] string? AccountNumber,
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("transactionDateTime")] string? TransactionDateTime,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("paymentLinkId")] string? PaymentLinkId,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("desc")] string? Desc)
{
    // PayOS can add fields to its callback payload. Keep them so signature
    // verification always covers the complete `data` object PayOS signed.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public record PayOsWebhookPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("desc")] string Desc,
    [property: JsonPropertyName("data")] PayOsWebhookData Data,
    [property: JsonPropertyName("signature")] string Signature);

public record ProcessPayOsWebhookCommand(
    PayOsWebhookPayload Payload,
    string? RawBody = null) : IRequest<Result<bool>>;

public sealed class ProcessPayOsWebhookCommandHandler(
    IPaymentCompletionProcessor completionProcessor,
    PaymentOptions paymentOptions,
    IApplicationDbContext dbContext,
    IDateTimeProvider? clock = null)
    : IRequestHandler<ProcessPayOsWebhookCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(
        ProcessPayOsWebhookCommand request,
        CancellationToken cancellationToken)
    {
        var payload = request.Payload;
        if (payload?.Data is null || string.IsNullOrWhiteSpace(payload.Signature))
        {
            return Result.Failure<bool>(
                PaymentErrorCodes.PaymentCallbackInvalid, "Invalid PayOS webhook payload structure.");
        }

        // The platform key cannot authorize a payment collected by a TO, even if reused by mistake.
        var operatorPayment = await dbContext.PaymentTransactions.AsNoTracking()
            .AnyAsync(t => t.OrderCode == payload.Data.OrderCode
                && (t.OperatorChannelId != null || t.Booking.PaymentFlow == PaymentFlow.OperatorDirect), cancellationToken);
        if (operatorPayment)
            return Result.Failure<bool>(PaymentErrorCodes.PaymentCallbackInvalid, "This payment requires its operator channel webhook.");

        // Verify signature if ChecksumKey is configured
        var checksumKey = paymentOptions.PayOs.ChecksumKey;
        if (string.IsNullOrWhiteSpace(checksumKey))
            return Result.Failure<bool>(PaymentErrorCodes.PaymentCallbackInvalid, "Webhook verification is not configured.");
        if (!string.IsNullOrWhiteSpace(checksumKey))
        {
            var isValid = VerifyPayOsSignature(payload.Data, payload.Signature, checksumKey);
            if (!isValid)
            {
                return Result.Failure<bool>(
                    PaymentErrorCodes.PaymentCallbackInvalid, "Invalid PayOS signature.");
            }
        }

        if (payload.Code != "00" || payload.Data.Code != "00")
            return Result.Failure<bool>(PaymentErrorCodes.PaymentCallbackInvalid, "Only successful payment notifications are accepted.");
        if (CommissionPaymentAttempt.IsCommissionOrderCode(payload.Data.OrderCode))
            return await CommissionPaymentSettlement.SettleAsync(dbContext, payload.Data.OrderCode, payload.Data.Amount,
                payload.Data.Currency, payload.Data.PaymentLinkId, payload.Data.Reference, PaymentGateway.PayOS,
                clock?.UtcNow ?? DateTimeOffset.UtcNow, cancellationToken);
        var outcome = PaymentTransactionStatus.Success;

        var rawResponse = !string.IsNullOrWhiteSpace(request.RawBody)
            ? request.RawBody
            : JsonSerializer.Serialize(payload);

        var completionResult = await completionProcessor.CompleteAsync(
            transactionId: null,
            orderCode: payload.Data.OrderCode,
            outcome: outcome,
            gatewayRef: payload.Data.Reference ?? payload.Data.PaymentLinkId,
            rawResponse: rawResponse,
            verifiedAmount: payload.Data.Amount,
            verifiedCurrency: payload.Data.Currency,
            cancellationToken: cancellationToken);

        if (!completionResult.IsSuccess)
        {
            // confirm-webhook verifies the URL by posting a signed sample
            // transaction. It is intentionally not a TripMate transaction, so
            // acknowledge that verified probe without creating or changing data.
            if (string.Equals(
                    completionResult.ErrorCode,
                    PaymentErrorCodes.PaymentNotFound,
                    StringComparison.Ordinal))
            {
                return Result.Success(true);
            }

            return Result.Failure<bool>(
                completionResult.ErrorCode ?? PaymentErrorCodes.PaymentCallbackInvalid,
                completionResult.ErrorMessage ?? "Payment completion failed.");
        }

        return Result.Success(true);
    }

    public static bool VerifyPayOsSignature(PayOsWebhookData data, string signature, string checksumKey)
    {
        var dataDict = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = ((long)data.Amount).ToString(),
            ["description"] = data.Description ?? string.Empty,
            ["orderCode"] = data.OrderCode.ToString(),
            ["currency"] = data.Currency ?? PaymentTransaction.DefaultCurrency,
            ["accountNumber"] = data.AccountNumber ?? string.Empty,
            ["reference"] = data.Reference ?? string.Empty,
            ["transactionDateTime"] = data.TransactionDateTime ?? string.Empty,
            ["paymentLinkId"] = data.PaymentLinkId ?? string.Empty,
            ["code"] = data.Code ?? string.Empty,
            ["desc"] = data.Desc ?? string.Empty,
        };

        foreach (var (key, value) in data.AdditionalProperties ?? [])
        {
            dataDict[key] = value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.String => value.GetString() ?? string.Empty,
                _ => value.GetRawText(),
            };
        }

        var sortedQuery = new StringBuilder();
        foreach (var (k, v) in dataDict)
        {
            if (sortedQuery.Length > 0) sortedQuery.Append('&');
            sortedQuery.Append(k).Append('=').Append(v);
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(checksumKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(sortedQuery.ToString()));
        var computedSignature = Convert.ToHexStringLower(hash);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedSignature),
            Encoding.UTF8.GetBytes(signature.ToLowerInvariant()));
    }
}