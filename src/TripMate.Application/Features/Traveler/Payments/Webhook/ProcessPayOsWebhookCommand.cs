using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using MediatR;

using TripMate.Application.Common.Models;
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
    [property: JsonPropertyName("desc")] string? Desc);

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
    PaymentOptions paymentOptions)
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

        // Verify signature if ChecksumKey is configured
        var checksumKey = paymentOptions.PayOs.ChecksumKey;
        if (!string.IsNullOrWhiteSpace(checksumKey))
        {
            var isValid = VerifyPayOsSignature(payload.Data, payload.Signature, checksumKey);
            if (!isValid)
            {
                return Result.Failure<bool>(
                    PaymentErrorCodes.PaymentCallbackInvalid, "Invalid PayOS signature.");
            }
        }

        var outcome = string.Equals(payload.Code, "00", StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(payload.Data.Code, "00", StringComparison.OrdinalIgnoreCase)
            ? PaymentTransactionStatus.Success
            : PaymentTransactionStatus.Cancelled;

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
            ["cancel"] = "false",
            ["description"] = data.Description ?? string.Empty,
            ["orderCode"] = data.OrderCode.ToString(),
            ["currency"] = data.Currency ?? PaymentTransaction.DefaultCurrency,
        };

        if (!string.IsNullOrWhiteSpace(data.AccountNumber))
            dataDict["accountNumber"] = data.AccountNumber;
        if (!string.IsNullOrWhiteSpace(data.Reference))
            dataDict["reference"] = data.Reference;
        if (!string.IsNullOrWhiteSpace(data.TransactionDateTime))
            dataDict["transactionDateTime"] = data.TransactionDateTime;
        if (!string.IsNullOrWhiteSpace(data.PaymentLinkId))
            dataDict["paymentLinkId"] = data.PaymentLinkId;
        if (!string.IsNullOrWhiteSpace(data.Code))
            dataDict["code"] = data.Code;
        if (!string.IsNullOrWhiteSpace(data.Desc))
            dataDict["desc"] = data.Desc;

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
