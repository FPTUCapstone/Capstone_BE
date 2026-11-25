using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.OperatorPaymentChannels. Represents a TO's payOS payment channel
/// (one per TO). Credential fields hold encrypted references to a secret store —
/// raw keys are never stored in the database.
/// Only a channel in status <see cref="StatusReady"/> may be used to create new payment links.
/// </summary>
public sealed class OperatorPaymentChannel : BaseEntity
{
    public const string StatusNotConnected = "NotConnected";
    public const string StatusPendingVerification = "PendingVerification";
    public const string StatusReady = "Ready";
    public const string StatusConnectionError = "ConnectionError";
    public const string StatusDisabled = "Disabled";

    public const int ClientIdRefMaxLength = 500;
    public const int ApiKeyRefMaxLength = 500;
    public const int ChecksumKeyRefMaxLength = 500;
    public const int BeneficiaryAccountNumberMaxLength = 50;
    public const int BeneficiaryAccountNameMaxLength = 200;
    public const int BeneficiaryBankCodeMaxLength = 20;
    public const int WebhookUrlMaxLength = 500;

    private OperatorPaymentChannel()
    {
    }

    public long TourOperatorUserId { get; private set; }

    public Guid OpaqueId { get; private set; }

    public string Status { get; private set; } = StatusNotConnected;

    public int ChannelVersion { get; private set; } = 1;

    /// <summary>Encrypted reference token; the raw Client ID lives in the secret store.</summary>
    public string? ClientIdRef { get; private set; }

    /// <summary>Encrypted reference token; the raw API Key lives in the secret store.</summary>
    public string? ApiKeyRef { get; private set; }

    /// <summary>Encrypted reference token; the raw Checksum Key lives in the secret store.</summary>
    public string? ChecksumKeyRef { get; private set; }

    /// <summary>Display-only beneficiary info snapshotted at verification time.</summary>
    public string? BeneficiaryAccountNumber { get; private set; }

    public string? BeneficiaryAccountName { get; private set; }

    public string? BeneficiaryBankCode { get; private set; }

    public DateTimeOffset? WebhookRegisteredAtUtc { get; private set; }

    public string? WebhookUrl { get; private set; }

    public DateTimeOffset? ConnectedAtUtc { get; private set; }

    public DateTimeOffset? LastVerifiedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static OperatorPaymentChannel Create(long tourOperatorUserId, DateTimeOffset nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tourOperatorUserId);
        return new OperatorPaymentChannel
        {
            TourOperatorUserId = tourOperatorUserId,
            OpaqueId = Guid.NewGuid(),
            Status = StatusNotConnected,
            ChannelVersion = 1,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    public void SetCredentialRefs(
        string clientIdRef, string apiKeyRef, string checksumKeyRef, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientIdRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksumKeyRef);

        ClientIdRef = clientIdRef;
        ApiKeyRef = apiKeyRef;
        ChecksumKeyRef = checksumKeyRef;
        Status = StatusPendingVerification;
        ChannelVersion++;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void MarkReady(
        string beneficiaryAccountNumber,
        string beneficiaryAccountName,
        string beneficiaryBankCode,
        string webhookUrl,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beneficiaryAccountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(beneficiaryAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(beneficiaryBankCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookUrl);

        BeneficiaryAccountNumber = beneficiaryAccountNumber;
        BeneficiaryAccountName = beneficiaryAccountName;
        BeneficiaryBankCode = beneficiaryBankCode;
        WebhookUrl = webhookUrl;
        WebhookRegisteredAtUtc = nowUtc.ToUniversalTime();
        LastVerifiedAtUtc = nowUtc.ToUniversalTime();
        ConnectedAtUtc ??= nowUtc.ToUniversalTime();
        Status = StatusReady;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void MarkConnectionError(DateTimeOffset nowUtc)
    {
        Status = StatusConnectionError;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void Disable(DateTimeOffset nowUtc)
    {
        Status = StatusDisabled;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }
}
