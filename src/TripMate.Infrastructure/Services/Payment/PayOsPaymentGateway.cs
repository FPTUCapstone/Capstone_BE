using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services.Payment;

public sealed class PayOsPaymentGateway(
    HttpClient httpClient,
    PaymentOptions options) : IPaymentGateway
{
    private const string PayOsApiBaseUrl = "https://api-merchant.payos.vn/v2/payment-requests";

    public PaymentGateway Gateway => PaymentGateway.PayOS;

    public async Task<PaymentLinkResult> CreatePaymentLinkAsync(
        PaymentLinkRequest request,
        CancellationToken cancellationToken)
    {
        var payOs = options.PayOs;
        if (string.IsNullOrWhiteSpace(payOs.ClientId) ||
            string.IsNullOrWhiteSpace(payOs.ApiKey) ||
            string.IsNullOrWhiteSpace(payOs.ChecksumKey))
        {
            throw new InvalidOperationException("PayOS credentials are not configured.");
        }

        var amountLong = (long)request.Amount;
        var cancelUrl = string.IsNullOrWhiteSpace(request.CancelUrl) ? payOs.CancelUrl : request.CancelUrl;
        var returnUrl = string.IsNullOrWhiteSpace(request.ReturnUrl) ? payOs.ReturnUrl : request.ReturnUrl;

        // Signature for create payment link:
        // amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}
        var signatureData = $"amount={amountLong}&cancelUrl={cancelUrl}&description={request.Description}&orderCode={request.OrderCode}&returnUrl={returnUrl}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(payOs.ChecksumKey));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureData)));

        var payload = new
        {
            orderCode = request.OrderCode,
            amount = amountLong,
            description = request.Description,
            cancelUrl = cancelUrl,
            returnUrl = returnUrl,
            signature = signature,
            expiredAt = request.ExpiresAtUtc?.ToUnixTimeSeconds(),
        };

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, PayOsApiBaseUrl);
        requestMessage.Headers.Add("x-client-id", payOs.ClientId);
        requestMessage.Headers.Add("x-api-key", payOs.ApiKey);
        requestMessage.Content = JsonContent.Create(payload);

        using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"PayOS create payment link failed with status {response.StatusCode}: {errorBody}");
        }

        var json = await response.Content.ReadFromJsonAsync<PayOsCreateResponse>(cancellationToken: cancellationToken);
        if (json is null)
        {
            throw new InvalidOperationException("PayOS returned an empty response when creating a payment link.");
        }

        if (!string.Equals(json.Code, "00", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PayOS rejected payment link creation (code {json.Code ?? "unknown"}): {json.Desc ?? "No description returned."}");
        }

        if (json.Data is null || string.IsNullOrWhiteSpace(json.Data.CheckoutUrl))
        {
            throw new InvalidOperationException("PayOS returned a successful response without checkoutUrl.");
        }

        DateTimeOffset? expiresAt = null;
        if (json.Data.ExpiredAt.HasValue && json.Data.ExpiredAt.Value > 0)
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(json.Data.ExpiredAt.Value);
        }

        return new PaymentLinkResult(
            PaymentLinkId: json.Data.PaymentLinkId ?? request.OrderCode.ToString(),
            CheckoutUrl: json.Data.CheckoutUrl,
            ExpiresAtUtc: expiresAt ?? request.ExpiresAtUtc,
            QrCode: json.Data.QrCode);
    }

    public async Task CancelPaymentLinkAsync(
        PaymentLinkCancellationRequest request,
        CancellationToken cancellationToken)
    {
        var payOs = options.PayOs;
        if (string.IsNullOrWhiteSpace(payOs.ClientId) ||
            string.IsNullOrWhiteSpace(payOs.ApiKey))
        {
            throw new InvalidOperationException("PayOS credentials are not configured.");
        }

        var paymentLinkIdentifier = string.IsNullOrWhiteSpace(request.PaymentLinkId)
            ? request.OrderCode.ToString()
            : request.PaymentLinkId;
        var url = $"{PayOsApiBaseUrl}/{Uri.EscapeDataString(paymentLinkIdentifier)}/cancel";

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
        requestMessage.Headers.Add("x-client-id", payOs.ClientId);
        requestMessage.Headers.Add("x-api-key", payOs.ApiKey);
        requestMessage.Content = JsonContent.Create(new
        {
            cancellationReason = string.IsNullOrWhiteSpace(request.Reason)
                ? "Traveler cancelled payment"
                : request.Reason,
        });

        using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"PayOS cancel payment link failed with status {response.StatusCode}: {errorBody}");
        }

        var json = await response.Content.ReadFromJsonAsync<PayOsCancelResponse>(cancellationToken: cancellationToken);
        if (json is null || !string.Equals(json.Code, "00", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PayOS rejected payment link cancellation (code {json?.Code ?? "unknown"}): {json?.Desc ?? "No description returned."}");
        }
    }

    // The documented PayOS API exposes payment-link operations and payouts, but not an API that
    // reverses a completed payment to its original payer. A payout requires beneficiary bank
    // details, which are not payment credentials and must not be invented from a transaction.
    // Ask operations to settle it manually instead of recording a false automatic refund.
    public Task<RefundSubmissionResult> SubmitRefundAsync(
        RefundSubmissionRequest request,
        CancellationToken cancellationToken) => Task.FromResult(new RefundSubmissionResult(
        false,
        FailureCode: "payment.refund.manual_review_required",
        FailureMessage: "PayOS refunds require manual operations review.",
        IsRetryable: false,
        RequiresManualReview: true));

    private sealed record PayOsCreateResponse(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("desc")] string? Desc,
        [property: JsonPropertyName("data")] PayOsCreateData? Data);

    private sealed record PayOsCreateData(
        [property: JsonPropertyName("paymentLinkId")] string? PaymentLinkId,
        [property: JsonPropertyName("checkoutUrl")] string? CheckoutUrl,
        [property: JsonPropertyName("qrCode")] string? QrCode,
        [property: JsonPropertyName("expiredAt")] long? ExpiredAt);

    private sealed record PayOsCancelResponse(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("desc")] string? Desc);
}