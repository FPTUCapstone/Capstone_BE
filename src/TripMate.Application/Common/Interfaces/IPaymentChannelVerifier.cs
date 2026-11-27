namespace TripMate.Application.Common.Interfaces;

/// <summary>
/// Verifies a payOS payment channel by calling the provider's test API and registering the
/// webhook. Implementations must not log credential values.
/// </summary>
public interface IPaymentChannelVerifier
{
    /// <param name="credentials">Raw (unprotected) credential values — never log or serialise.</param>
    /// <param name="webhookBaseUrl">Full server-built channel webhook URL the provider will POST events to.</param>
    Task<ChannelVerificationResult> VerifyAsync(
        PaymentChannelCredentials credentials,
        string webhookBaseUrl,
        CancellationToken cancellationToken);
}

public sealed record PaymentChannelCredentials(
    string ClientId,
    string ApiKey,
    string ChecksumKey,
    string BeneficiaryAccountNumber,
    string BeneficiaryAccountName,
    string BeneficiaryBankCode);

public sealed record ChannelVerificationResult(
    ChannelVerificationOutcome Outcome,
    string? WebhookUrl = null,
    string? ErrorCode = null,
    string? BeneficiaryAccountNumber = null,
    string? BeneficiaryAccountName = null);

public enum ChannelVerificationOutcome
{
    /// <summary>API call succeeded and beneficiary matches the business profile.</summary>
    AutoApproved,

    /// <summary>API call succeeded but beneficiary ownership requires manual Admin review.</summary>
    PendingAdminApproval,

    /// <summary>API call failed (network error, invalid credentials, provider error).</summary>
    ConnectionError,
}
