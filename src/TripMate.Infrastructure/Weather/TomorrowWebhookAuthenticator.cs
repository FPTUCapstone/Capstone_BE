using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Weather;

/// <summary>
/// Verifies <c>X-Signature: t={timestamp},sig={hex}</c> as documented at
/// https://docs.tomorrow.io/reference/api-webhooks: the signature is HMAC-SHA256 over the raw
/// timestamp string with the webhook secret as key. The body is not signed.
/// </summary>
public sealed class TomorrowWebhookAuthenticator : ITomorrowWebhookAuthenticator
{
    // Unix seconds stay below this until the year 5138; a larger value can only be milliseconds.
    private const long MillisecondTimestampThreshold = 100_000_000_000;
    private const int Sha256HexLength = 64;

    private readonly TomorrowIoOptions _options;
    private readonly TimeProvider _timeProvider;

    public TomorrowWebhookAuthenticator(
        IOptions<TomorrowIoOptions> options,
        TimeProvider? timeProvider = null)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool ValidateSignature(
        string? signatureHeader,
        DateTimeOffset? utcNow,
        out string? failureReason)
    {
        failureReason = null;

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            failureReason = "Tomorrow.io webhook secret is not configured.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            failureReason = "Signature header is missing or empty.";
            return false;
        }

        if (!TryParseSignatureHeader(signatureHeader, out string timestamp, out long timestampValue,
                out byte[] signature))
        {
            failureReason = "Malformed signature header. Expected format: t=<timestamp>,sig=<hex>.";
            return false;
        }

        // The contract does not name the unit of t, so freshness accepts seconds or milliseconds.
        var headerTime = timestampValue >= MillisecondTimestampThreshold
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestampValue)
            : DateTimeOffset.FromUnixTimeSeconds(timestampValue);
        var currentTime = utcNow ?? _timeProvider.GetUtcNow();
        var tolerance = TimeSpan.FromSeconds(Math.Max(1, _options.WebhookToleranceSeconds));

        if (headerTime < currentTime - tolerance)
        {
            failureReason = $"Webhook signature timestamp is stale (tolerance {tolerance.TotalSeconds}s).";
            return false;
        }

        if (headerTime > currentTime + tolerance)
        {
            failureReason = $"Webhook signature timestamp is too far in the future (tolerance {tolerance.TotalSeconds}s).";
            return false;
        }

        // The raw timestamp string is signed exactly as received.
        byte[] expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.WebhookSecret), Encoding.UTF8.GetBytes(timestamp));

        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            failureReason = "Signature hash mismatch.";
            return false;
        }

        return true;
    }

    private static bool TryParseSignatureHeader(
        string header,
        out string timestamp,
        out long timestampValue,
        out byte[] signature)
    {
        timestamp = string.Empty;
        timestampValue = 0;
        signature = [];
        string? signatureHex = null;

        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) continue;

            if (pair[0] == "t") timestamp = pair[1];
            else if (pair[0] == "sig") signatureHex = pair[1];
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out timestampValue)
            || timestampValue <= 0
            || signatureHex is not { Length: Sha256HexLength })
        {
            return false;
        }

        try
        {
            signature = Convert.FromHexString(signatureHex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
