using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Services.Payment;

/// <summary>
/// payOS gateway instance for OperatorDirect bookings. Uses per-TO credentials
/// supplied via OperatorChannelSnapshot — never platform PaymentOptions.
/// </summary>
public sealed class OperatorDirectPayOsGateway(
    HttpClient httpClient,
    OperatorChannelSnapshot snapshot) : IPaymentGateway
{
    private const string PayOsApiBaseUrl = "https://api-merchant.payos.vn/v2/payment-requests";

    public PaymentGateway Gateway => PaymentGateway.PayOS;

    public async Task<PaymentLinkResult> CreatePaymentLinkAsync(
        PaymentLinkRequest request,
        CancellationToken cancellationToken)
    {
        var amountLong = (long)request.Amount;
        var cancelUrl = string.IsNullOrWhiteSpace(request.CancelUrl) ? snapshot.CancelUrl : request.CancelUrl;
        var returnUrl = string.IsNullOrWhiteSpace(request.ReturnUrl) ? snapshot.ReturnUrl : request.ReturnUrl;

        var signatureData =
            $"amount={amountLong}&cancelUrl={cancelUrl}&description={request.Description}&orderCode={request.OrderCode}&returnUrl={returnUrl}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(snapshot.ChecksumKey));
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
        requestMessage.Headers.Add("x-client-id", snapshot.ClientId);
        requestMessage.Headers.Add("x-api-key", snapshot.ApiKey);
        requestMessage.Content = JsonContent.Create(payload);

        using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"PayOS (operator) create payment link failed with status {response.StatusCode}: {errorBody}");
        }

        var json = await response.Content.ReadFromJsonAsync<PayOsCreateResponse>(
            cancellationToken: cancellationToken);
        if (json is null)
            throw new InvalidOperationException("PayOS (operator) returned an empty response.");

        if (!string.Equals(json.Code, "00", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PayOS (operator) rejected payment link creation (code {json.Code ?? "unknown"}): {json.Desc ?? "No description."}");

        if (json.Data is null || string.IsNullOrWhiteSpace(json.Data.CheckoutUrl))
            throw new InvalidOperationException("PayOS (operator) returned a successful response without checkoutUrl.");

        DateTimeOffset? expiresAt = null;
        if (json.Data.ExpiredAt.HasValue && json.Data.ExpiredAt.Value > 0)
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(json.Data.ExpiredAt.Value);

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
        var identifier = string.IsNullOrWhiteSpace(request.PaymentLinkId)
            ? request.OrderCode.ToString()
            : request.PaymentLinkId;
        var url = $"{PayOsApiBaseUrl}/{Uri.EscapeDataString(identifier)}/cancel";

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
        requestMessage.Headers.Add("x-client-id", snapshot.ClientId);
        requestMessage.Headers.Add("x-api-key", snapshot.ApiKey);
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
            throw new HttpRequestException(
                $"PayOS (operator) cancel payment link failed with status {response.StatusCode}: {errorBody}");
        }

        var json = await response.Content.ReadFromJsonAsync<PayOsCancelResponse>(cancellationToken: cancellationToken);
        if (json is null || !string.Equals(json.Code, "00", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"PayOS (operator) rejected cancel (code {json?.Code ?? "unknown"}): {json?.Desc ?? "No description."}");
    }

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
