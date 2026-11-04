namespace TripMate.Application.Common.Interfaces;

public interface ITomorrowWebhookAuthenticator
{
    bool ValidateSignature(
        string payload,
        string? signatureHeader,
        DateTimeOffset? utcNow,
        out string? failureReason);

    string GenerateSignatureHeader(string payload, DateTimeOffset timestampUtc);
}
