using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using TripMate.Application.Features.Admin.PayoutSettlements.Processing;

namespace TripMate.Infrastructure.Services.Payment.PayoutDisbursement;

/// <summary>
/// Pays Operators through the PayOS Payouts API (UC-65). Wire facts come from the PayOS API
/// documentation and the official PayOS .NET SDK; the failure-state vocabulary and the idempotency
/// and lookup behaviour were confirmed by the team. PayOS has no sandbox and no payout webhook, so
/// results are read by polling. Request and response bodies, credentials and account numbers are
/// never logged and never copied into the normalized results.
/// </summary>
public sealed class PayOsPayoutGateway(
    HttpClient httpClient,
    PayoutDisbursementOptions options,
    ILogger<PayOsPayoutGateway> logger) : IPayoutDisbursementGateway
{
    private const string SuccessCode = "00";
    private const int MaxClassifiedBodyLength = 2_000;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 16 };

    public async Task<PayoutSubmitResult> SubmitAsync(PayoutSubmitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0m || request.Amount != decimal.Truncate(request.Amount))
        {
            return PayoutSubmitResult.Rejected("gateway_invalid_amount");
        }

        var amount = (long)request.Amount;
        var description = PayoutDescription.Sanitize(request.Description);
        var signed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["referenceId"] = request.ReferenceId,
            ["amount"] = amount.ToString(CultureInfo.InvariantCulture),
            ["description"] = description,
            ["toBin"] = request.ToBin,
            ["toAccountNumber"] = request.ToAccountNumber,
            // The SDK sends and signs "category" even when unused: null in the body, empty when signed.
            ["category"] = string.Empty,
        };
        var body = JsonSerializer.Serialize(new
        {
            referenceId = request.ReferenceId,
            amount,
            description,
            toBin = request.ToBin,
            toAccountNumber = request.ToAccountNumber,
            category = (string[]?)null,
        });

        using var message = new HttpRequestMessage(HttpMethod.Post, Url("/v1/payouts"));
        AddCredentials(message);
        message.Headers.Add("x-signature", PayOsPayoutSignature.Compute(options.PayOs.ChecksumKey, signed));
        message.Headers.Add("x-idempotency-key", PayOsPayoutSignature.IdempotencyHeaderValue(request.IdempotencyKey));
        message.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return PayoutSubmitResult.Transient("gateway_unavailable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PayoutSubmitResult.Transient("gateway_unavailable");
        }

        using (response)
        {
            return await InterpretSubmitAsync(response, cancellationToken);
        }
    }

    public async Task<PayoutStatusResult> GetStatusAsync(string gatewayPayoutId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayPayoutId);
        var data = await GetDataAsync($"/v1/payouts/{Uri.EscapeDataString(gatewayPayoutId)}", cancellationToken);
        using (data)
        {
            return data is null
                ? PayoutStatusResult.Unavailable()
                : PayoutStatusResult.Of(ReadState(data.RootElement));
        }
    }

    public async Task<PayoutLookupResult> FindByReferenceAsync(string referenceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        var data = await GetDataAsync($"/v1/payouts?referenceId={Uri.EscapeDataString(referenceId)}", cancellationToken);
        using (data)
        {
            if (data is null || !data.RootElement.TryGetProperty("payouts", out var payouts))
            {
                // Without a payouts collection the answer says nothing about whether it exists.
                return PayoutLookupResult.Failed();
            }

            foreach (var payout in Items(payouts))
            {
                if (payout.ValueKind == JsonValueKind.Object
                    && payout.TryGetProperty("referenceId", out var reference)
                    && reference.ValueKind == JsonValueKind.String
                    && string.Equals(reference.GetString(), referenceId, StringComparison.Ordinal))
                {
                    var id = payout.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    return string.IsNullOrWhiteSpace(id)
                        ? PayoutLookupResult.Failed()
                        : PayoutLookupResult.Found(id, ReadState(payout));
                }
            }

            return PayoutLookupResult.NotFound();
        }
    }

    private async Task<PayoutSubmitResult> InterpretSubmitAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (status is 408 or 429 || status >= 500)
        {
            return PayoutSubmitResult.Transient("gateway_unavailable");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return PayoutSubmitResult.Rejected("gateway_unauthorized");
        }

        var text = await ReadBodyAsync(response, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            if (!TryParse(text, out var document))
            {
                return PayoutSubmitResult.Transient("gateway_malformed_response");
            }

            using (document)
            {
                var root = document!.RootElement;
                if (StringProperty(root, "code") == SuccessCode)
                {
                    var id = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                        ? StringProperty(data, "id")
                        : null;
                    // Accepted but unidentified: the transfer may exist, so this is not a rejection.
                    return string.IsNullOrWhiteSpace(id)
                        ? PayoutSubmitResult.Transient("gateway_malformed_response")
                        : PayoutSubmitResult.Accepted(id);
                }

                return ClassifyRejection(Description(root) ?? text);
            }
        }

        string? description = null;
        if (TryParse(text, out var errorDocument))
        {
            using (errorDocument)
            {
                description = Description(errorDocument!.RootElement);
            }
        }

        return ClassifyRejection(description ?? text);
    }

    private static PayoutSubmitResult ClassifyRejection(string? description)
    {
        var text = (description ?? string.Empty).Normalize(NormalizationForm.FormC);
        if (text.Length > MaxClassifiedBodyLength)
        {
            text = text[..MaxClassifiedBodyLength];
        }

        if (Contains(text, "idempotency"))
        {
            return PayoutSubmitResult.KeyExists();
        }

        if (Contains(text, "Số dư không đủ") || (Contains(text, "insufficient") && Contains(text, "balance")))
        {
            return PayoutSubmitResult.Rejected("gateway_insufficient_balance");
        }

        if (Contains(text, "Tài khoản") && (Contains(text, "không hợp lệ") || Contains(text, "không tìm thấy")))
        {
            return PayoutSubmitResult.Rejected("gateway_invalid_account");
        }

        if (Contains(text, "signature"))
        {
            return PayoutSubmitResult.Rejected("gateway_invalid_signature");
        }

        return PayoutSubmitResult.Rejected("gateway_rejected");
    }

    /// <summary>GETs a PayOS endpoint and returns its <c>data</c> element, or null when the call
    /// failed, was not a success envelope, or was malformed. The caller disposes the document.</summary>
    private async Task<JsonDocument?> GetDataAsync(string path, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, Url(path));
        AddCredentials(message);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await ReadBodyAsync(response, cancellationToken);
            if (!TryParse(text, out var document))
            {
                return null;
            }

            if (StringProperty(document!.RootElement, "code") != SuccessCode
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return null;
            }

            // Re-parse just the data element so the caller owns a self-contained document.
            var detached = JsonDocument.Parse(data.GetRawText(), JsonOptions);
            document.Dispose();
            return detached;
        }
    }

    /// <summary>
    /// Maps a payout object to a state. Transaction states decide when present: any FAILED or
    /// CANCELLED, REJECTED or REVERSED wins (all terminal failures), SUCCEEDED requires every
    /// transaction to have succeeded, PROCESSING stays processing, and anything else (RECEIVED,
    /// ON_HOLD, new values) is Unknown so the caller
    /// keeps polling and the 24 hour limit hands it to a person. The approval state alone only ever
    /// reports failure or processing, never success.
    /// </summary>
    private PayoutGatewayState ReadState(JsonElement payout)
    {
        var transactionStates = payout.TryGetProperty("transactions", out var transactions)
            ? Items(transactions)
                .Select(transaction => transaction.ValueKind == JsonValueKind.Object ? StringProperty(transaction, "state") : null)
                .ToList()
            : [];

        PayoutGatewayState state;
        if (transactionStates.Count > 0)
        {
            state = transactionStates switch
            {
                _ when transactionStates.Any(item => IsState(item, "FAILED")) => PayoutGatewayState.Failed,
                _ when transactionStates.Any(item => IsState(item, "CANCELLED")) => PayoutGatewayState.Cancelled,
                _ when transactionStates.Any(item => IsState(item, "REJECTED")) => PayoutGatewayState.Rejected,
                _ when transactionStates.Any(item => IsState(item, "REVERSED")) => PayoutGatewayState.Reversed,
                _ when transactionStates.All(item => IsState(item, "SUCCEEDED")) => PayoutGatewayState.Succeeded,
                _ when transactionStates.Any(item => IsState(item, "PROCESSING")) => PayoutGatewayState.Processing,
                _ => PayoutGatewayState.Unknown,
            };
        }
        else
        {
            var approval = StringProperty(payout, "approvalState");
            state = approval switch
            {
                _ when IsState(approval, "FAILED") => PayoutGatewayState.Failed,
                _ when IsState(approval, "CANCELLED") => PayoutGatewayState.Cancelled,
                _ when IsState(approval, "REJECTED") => PayoutGatewayState.Rejected,
                _ when IsState(approval, "REVERSED") => PayoutGatewayState.Reversed,
                _ when IsState(approval, "PROCESSING") => PayoutGatewayState.Processing,
                _ => PayoutGatewayState.Unknown,
            };
        }

        if (state == PayoutGatewayState.Unknown)
        {
            logger.LogWarning(
                "PayOS payout returned a state outside the confirmed contract: {States}. Treating it as still processing.",
                string.Join(",", transactionStates.Select(item => Truncate(item))));
        }

        return state;
    }

    private static IEnumerable<JsonElement> Items(JsonElement collection) => collection.ValueKind switch
    {
        JsonValueKind.Array => collection.EnumerateArray(),
        JsonValueKind.Object => collection.EnumerateObject().Select(property => property.Value),
        _ => [],
    };

    private static bool IsState(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string text, string fragment) =>
        text.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Description(JsonElement root) => StringProperty(root, "desc") ?? StringProperty(root, "message");

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? "(none)" : value.Length > 40 ? value[..40] : value;

    private static bool TryParse(string text, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(text, JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
    }

    private string Url(string path) => options.PayOs.BaseUrl.TrimEnd('/') + path;

    private void AddCredentials(HttpRequestMessage message)
    {
        message.Headers.Add("x-client-id", options.PayOs.ClientId);
        message.Headers.Add("x-api-key", options.PayOs.ApiKey);
    }
}