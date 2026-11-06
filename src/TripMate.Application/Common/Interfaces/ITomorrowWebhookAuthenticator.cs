namespace TripMate.Application.Common.Interfaces;

public interface ITomorrowWebhookAuthenticator
{
    /// <summary>
    /// Verifies the <c>X-Signature</c> header. Tomorrow.io signs the timestamp only, so the
    /// request body takes no part in the check.
    /// </summary>
    bool ValidateSignature(
        string? signatureHeader,
        DateTimeOffset? utcNow,
        out string? failureReason);
}