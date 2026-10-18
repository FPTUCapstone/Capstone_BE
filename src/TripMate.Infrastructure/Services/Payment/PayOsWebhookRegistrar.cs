using System.Net.Http.Json;

using TripMate.Application.Features.Traveler.Payments.Common;

namespace TripMate.Infrastructure.Services.Payment;

/// <summary>
/// Registers a public HTTPS webhook URL with PayOS as a one-time operational step.
/// </summary>
public sealed class PayOsWebhookRegistrar(HttpClient httpClient, PaymentOptions options)
{
    private const string ConfirmWebhookUrl = "https://api-merchant.payos.vn/confirm-webhook";

    public async Task ConfirmAsync(Uri webhookUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookUrl);

        if (!webhookUrl.IsAbsoluteUri || !string.Equals(webhookUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The PayOS webhook URL must be an absolute HTTPS URL.", nameof(webhookUrl));
        }

        var payOs = options.PayOs;
        if (string.IsNullOrWhiteSpace(payOs.ClientId) || string.IsNullOrWhiteSpace(payOs.ApiKey))
        {
            throw new InvalidOperationException("PayOS ClientId and ApiKey must be configured before registering a webhook.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ConfirmWebhookUrl);
        request.Headers.Add("x-client-id", payOs.ClientId);
        request.Headers.Add("x-api-key", payOs.ApiKey);
        request.Content = JsonContent.Create(new { webhookUrl = webhookUrl.AbsoluteUri });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"PayOS webhook registration failed with status {(int)response.StatusCode}.");
        }
    }
}