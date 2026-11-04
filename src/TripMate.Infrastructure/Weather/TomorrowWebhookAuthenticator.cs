using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Interfaces;

namespace TripMate.Infrastructure.Weather;

public sealed class TomorrowWebhookAuthenticator : ITomorrowWebhookAuthenticator
{
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
        string payload,
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

        if (!TryParseSignatureHeader(signatureHeader, out long timestampSeconds, out string? signatureHex))
        {
            failureReason = "Malformed signature header. Expected format: t=<timestamp>,sig=<hex> or t=<timestamp>,v1=<hex>";
            return false;
        }

        var currentTime = utcNow ?? _timeProvider.GetUtcNow();
        var headerTime = DateTimeOffset.FromUnixTimeSeconds(timestampSeconds);
        var tolerance = TimeSpan.FromSeconds(Math.Max(1, _options.WebhookToleranceSeconds));

        if (headerTime < currentTime - tolerance)
        {
            failureReason = $"Webhook signature timestamp is stale. Header: {headerTime:O}, Current: {currentTime:O}, Tolerance: {tolerance.TotalSeconds}s";
            return false;
        }

        if (headerTime > currentTime + tolerance)
        {
            failureReason = $"Webhook signature timestamp is too far in the future. Header: {headerTime:O}, Current: {currentTime:O}, Tolerance: {tolerance.TotalSeconds}s";
            return false;
        }

        string expectedSignatureHex = ComputeSignatureHex(payload, timestampSeconds, _options.WebhookSecret);

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedSignatureHex);
        byte[] actualBytes = Encoding.UTF8.GetBytes(signatureHex!.ToLowerInvariant());

        if (expectedBytes.Length != actualBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
        {
            failureReason = "Signature hash mismatch.";
            return false;
        }

        return true;
    }

    public string GenerateSignatureHeader(string payload, DateTimeOffset timestampUtc)
    {
        long timestampSeconds = timestampUtc.ToUnixTimeSeconds();
        string signatureHex = ComputeSignatureHex(payload, timestampSeconds, _options.WebhookSecret);
        return $"t={timestampSeconds},sig={signatureHex}";
    }

    private static bool TryParseSignatureHeader(
        string header,
        out long timestampSeconds,
        out string? signatureHex)
    {
        timestampSeconds = 0;
        signatureHex = null;

        var parts = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;

            if (kv[0].Equals("t", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(kv[1], out timestampSeconds))
                {
                    return false;
                }
            }
            else if (kv[0].Equals("sig", StringComparison.OrdinalIgnoreCase) ||
                     kv[0].Equals("v1", StringComparison.OrdinalIgnoreCase))
            {
                signatureHex = kv[1];
            }
        }

        return timestampSeconds > 0 && !string.IsNullOrWhiteSpace(signatureHex);
    }

    private static string ComputeSignatureHex(string payload, long timestampSeconds, string secret)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(secret);
        byte[] dataBytes = Encoding.UTF8.GetBytes($"{timestampSeconds}.{payload}");

        using var hmac = new HMACSHA256(keyBytes);
        byte[] hash = hmac.ComputeHash(dataBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
