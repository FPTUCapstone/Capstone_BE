using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class OperatorContract : BaseEntity
{
    private OperatorContract() { }
    public long OperatorUserId { get; private set; }
    public long TemplateId { get; private set; }
    public int TemplateVersion { get; private set; }
    public string TemplateSourceStorageKey { get; private set; } = string.Empty;
    public string TemplateSourceSha256 { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string CompanyName { get; private set; } = string.Empty;
    public string TaxCode { get; private set; } = string.Empty;
    public string BusinessLicenseNo { get; private set; } = string.Empty;
    public string BusinessAddress { get; private set; } = string.Empty;
    public string OperatorEmail { get; private set; } = string.Empty;
    public string SignerName { get; private set; } = string.Empty;
    public string SignerTitle { get; private set; } = string.Empty;
    public string SignerCapacity { get; private set; } = string.Empty;
    public OperatorContractStatus Status { get; private set; }
    public OperatorContractDocumentState DocumentState { get; private set; }
    public int Revision { get; private set; } = 1;
    public string? SnapshotSha256 { get; private set; }
    public string? UnsignedDocumentStorageKey { get; private set; }
    public string? UnsignedDocumentSha256 { get; private set; }
    public string? SignatureAnchorsJson { get; private set; }
    public string? SignedDocumentStorageKey { get; private set; }
    public string? SignedDocumentSha256 { get; private set; }
    public string? SignatureStorageKey { get; private set; }
    public string? SignatureSha256 { get; private set; }
    public DateTimeOffset? SignedAtUtc { get; private set; }
    public long ApprovedByUserId { get; private set; }
    public DateTimeOffset ApprovedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? VoidedAtUtc { get; private set; }
    public Guid? PreparationClaimToken { get; private set; }
    public DateTimeOffset? PreparationLeaseExpiresAtUtc { get; private set; }
    public int PreparationAttempts { get; private set; }
    public string? PreparationErrorCode { get; private set; }
    public Guid? CurrentSigningChallengeId { get; private set; }
    public DateTimeOffset? LastOtpIssuedAtUtc { get; private set; }
    public Guid? SigningOperationId { get; private set; }
    public string? SigningRequestSha256 { get; private set; }
    public string? ConsentVersion { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static OperatorContract Create(long operatorUserId, long templateId, int templateVersion,
        string templateSourceStorageKey, string templateSourceSha256, string title, string companyName,
        string taxCode, string businessLicenseNo, string businessAddress, string operatorEmail,
        long approvedByUserId, DateTimeOffset now)
    {
        if (operatorUserId <= 0 || templateId <= 0 || templateVersion <= 0 || approvedByUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(operatorUserId));
        return new OperatorContract
        {
            OperatorUserId = operatorUserId,
            TemplateId = templateId,
            TemplateVersion = templateVersion,
            TemplateSourceStorageKey = Text(templateSourceStorageKey, 500),
            TemplateSourceSha256 = Hash(templateSourceSha256),
            Title = Text(title, 200),
            CompanyName = Text(companyName, 200),
            TaxCode = Text(taxCode, 50),
            BusinessLicenseNo = Text(businessLicenseNo, 100),
            BusinessAddress = OptionalText(businessAddress, 300),
            OperatorEmail = Text(operatorEmail, 256),
            ApprovedByUserId = approvedByUserId,
            ApprovedAtUtc = now.ToUniversalTime(),
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(),
            Status = OperatorContractStatus.PendingSignature,
            DocumentState = OperatorContractDocumentState.NeedsSignerDetails
        };
    }

    public bool TryStartPreparation(int expectedRevision, string signerName, string signerTitle, string signerCapacity, DateTimeOffset now)
    {
        if (Status != OperatorContractStatus.PendingSignature || Revision != expectedRevision ||
            DocumentState is not (OperatorContractDocumentState.NeedsSignerDetails or OperatorContractDocumentState.Failed)) return false;
        SignerName = Text(signerName, 150); SignerTitle = Text(signerTitle, 150); SignerCapacity = Text(signerCapacity, 500);
        Revision = checked(Revision + 1); DocumentState = OperatorContractDocumentState.Processing;
        SnapshotSha256 = null; UnsignedDocumentStorageKey = null; UnsignedDocumentSha256 = null; SignatureAnchorsJson = null;
        CurrentSigningChallengeId = null; PreparationClaimToken = null; PreparationLeaseExpiresAtUtc = null;
        PreparationAttempts = 0; PreparationErrorCode = null; UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }

    public bool TryClaimPreparation(Guid token, DateTimeOffset now, TimeSpan lease)
    {
        if (Status != OperatorContractStatus.PendingSignature || DocumentState != OperatorContractDocumentState.Processing ||
            PreparationAttempts >= 3 || PreparationLeaseExpiresAtUtc > now) return false;
        if (token == Guid.Empty || lease <= TimeSpan.Zero) throw new ArgumentException("A preparation lease is required.");
        PreparationClaimToken = token; PreparationLeaseExpiresAtUtc = now.ToUniversalTime().Add(lease);
        PreparationAttempts++; UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }

    public bool TryCompletePreparation(Guid token, int revision, string snapshotSha256, string unsignedKey,
        string unsignedSha256, string anchorsJson, DateTimeOffset now)
    {
        if (!OwnsPreparation(token, revision, now)) return false;
        SnapshotSha256 = Hash(snapshotSha256); UnsignedDocumentStorageKey = Text(unsignedKey, 500);
        UnsignedDocumentSha256 = Hash(unsignedSha256); SignatureAnchorsJson = Text(anchorsJson, 16000);
        DocumentState = OperatorContractDocumentState.Ready; PreparationClaimToken = null;
        PreparationLeaseExpiresAtUtc = null; PreparationErrorCode = null; UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }

    public bool TryFailPreparation(Guid token, int revision, string errorCode, DateTimeOffset now)
    {
        if (!OwnsPreparation(token, revision, now)) return false;
        return FailPreparation(errorCode, now);
    }

    public bool TryFailExhaustedPreparation(DateTimeOffset now, int maxAttempts, string errorCode)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (Status != OperatorContractStatus.PendingSignature || DocumentState != OperatorContractDocumentState.Processing ||
            PreparationAttempts < maxAttempts || PreparationLeaseExpiresAtUtc is null || PreparationLeaseExpiresAtUtc > now) return false;
        return FailPreparation(errorCode, now);
    }

    private bool FailPreparation(string errorCode, DateTimeOffset now)
    {
        PreparationErrorCode = Text(errorCode, 100); DocumentState = OperatorContractDocumentState.Failed;
        PreparationClaimToken = null; PreparationLeaseExpiresAtUtc = null; UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }

    public bool TryReserveChallenge(Guid challengeId, DateTimeOffset now, TimeSpan resendDelay)
    {
        if (Status != OperatorContractStatus.PendingSignature || DocumentState != OperatorContractDocumentState.Ready ||
            (LastOtpIssuedAtUtc is { } issued && now < issued.Add(resendDelay))) return false;
        if (challengeId == Guid.Empty || resendDelay < TimeSpan.Zero) throw new ArgumentException("Valid challenge and cooldown required.");
        CurrentSigningChallengeId = challengeId; LastOtpIssuedAtUtc = now.ToUniversalTime(); UpdatedAtUtc = now.ToUniversalTime();
        return true;
    }

    public bool TrySign(Guid challengeId, Guid signingOperationId, string signingRequestSha256, string expectedContentSha256,
        string signedKey, string signedSha256, string signatureKey, string signatureSha256, string consentVersion, DateTimeOffset now)
    {
        if (Status != OperatorContractStatus.PendingSignature || DocumentState != OperatorContractDocumentState.Ready ||
            CurrentSigningChallengeId != challengeId || string.IsNullOrWhiteSpace(UnsignedDocumentSha256) ||
            !string.Equals(UnsignedDocumentSha256, expectedContentSha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (challengeId == Guid.Empty || signingOperationId == Guid.Empty) throw new ArgumentException("Signing identity required.");
        SigningOperationId = signingOperationId; SigningRequestSha256 = Hash(signingRequestSha256);
        SignedDocumentStorageKey = Text(signedKey, 500); SignedDocumentSha256 = Hash(signedSha256);
        SignatureStorageKey = Text(signatureKey, 500); SignatureSha256 = Hash(signatureSha256);
        ConsentVersion = Text(consentVersion, 80); SignedAtUtc = now.ToUniversalTime(); UpdatedAtUtc = now.ToUniversalTime();
        Status = OperatorContractStatus.Signed;
        return true;
    }

    public bool TryVoid(DateTimeOffset now)
    {
        if (Status != OperatorContractStatus.PendingSignature) return false;
        Status = OperatorContractStatus.Voided; VoidedAtUtc = now.ToUniversalTime(); UpdatedAtUtc = now.ToUniversalTime();
        CurrentSigningChallengeId = null; PreparationClaimToken = null; PreparationLeaseExpiresAtUtc = null;
        return true;
    }

    private bool OwnsPreparation(Guid token, int revision, DateTimeOffset now) =>
        Status == OperatorContractStatus.PendingSignature && DocumentState == OperatorContractDocumentState.Processing &&
        Revision == revision && PreparationClaimToken == token && PreparationLeaseExpiresAtUtc > now;

    private static string Text(string value, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Trim().Length > maxLength) throw new ArgumentOutOfRangeException(nameof(value));
        return value.Trim();
    }
    private static string OptionalText(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? string.Empty : Text(value, maxLength);
    private static string Hash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 hash is required.", nameof(value));
        return value.ToLowerInvariant();
    }
}