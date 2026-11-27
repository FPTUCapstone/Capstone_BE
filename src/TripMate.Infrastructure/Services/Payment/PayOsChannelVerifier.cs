using System.Net.Http.Json;
using System.Text.Json;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Services.Payment;

/// <summary>Registers a TO webhook with payOS; legal ownership still requires Admin review.</summary>
public sealed class PayOsChannelVerifier(HttpClient httpClient) : IPaymentChannelVerifier
{
    public async Task<ChannelVerificationResult> VerifyAsync(
        PaymentChannelCredentials credentials, string webhookBaseUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(webhookBaseUrl, UriKind.Absolute, out var webhook)
            || webhook.Scheme != Uri.UriSchemeHttps || webhook.IsLoopback
            || !string.IsNullOrEmpty(webhook.UserInfo) || !string.IsNullOrEmpty(webhook.Query)
            || !string.IsNullOrEmpty(webhook.Fragment)
            || string.IsNullOrWhiteSpace(credentials.ClientId)
            || string.IsNullOrWhiteSpace(credentials.ApiKey)
            || string.IsNullOrWhiteSpace(credentials.ChecksumKey)
            || credentials.ClientId.IndexOfAny(['\r', '\n']) >= 0
            || credentials.ApiKey.IndexOfAny(['\r', '\n']) >= 0)
            return Failed("invalid_configuration");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api-merchant.payos.vn/confirm-webhook");
            request.Headers.Add("x-client-id", credentials.ClientId);
            request.Headers.Add("x-api-key", credentials.ApiKey);
            request.Content = JsonContent.Create(new { webhookUrl = webhook.AbsoluteUri });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return Failed("provider_rejected");
            // Provider response text can contain private account information; never expose or log it.
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out var code) || code.GetString() != "00"
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("webhookUrl", out var returnedUrl) || returnedUrl.GetString() != webhook.AbsoluteUri
                || !data.TryGetProperty("accountNumber", out var number) || string.IsNullOrWhiteSpace(number.GetString())
                || !data.TryGetProperty("accountName", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
                return Failed("invalid_provider_response");

            return new ChannelVerificationResult(ChannelVerificationOutcome.PendingAdminApproval,
                webhook.AbsoluteUri, BeneficiaryAccountNumber: number.GetString(), BeneficiaryAccountName: name.GetString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failed("provider_timeout"); }
        catch (HttpRequestException) { return Failed("provider_unavailable"); }
        catch (JsonException) { return Failed("invalid_provider_response"); }
        catch (InvalidOperationException) { return Failed("invalid_provider_response"); }
        catch (FormatException) { return Failed("invalid_credentials"); }
    }

    private static ChannelVerificationResult Failed(string code) => new(ChannelVerificationOutcome.ConnectionError, ErrorCode: code);
}