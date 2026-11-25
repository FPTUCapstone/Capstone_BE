namespace TripMate.Application.Features.Operator.Contracts.Common;

public sealed record ContractOtpPolicyDto(int TimeToLiveSeconds, int ResendCooldownSeconds, int MaxFailedAttempts, string ConsentVersion);

public sealed record OperatorContractDto(
    string Id, string Status, string DocumentState, string Title, int TemplateVersion,
    string CompanyName, string TaxCode, string BusinessLicenseNo, string BusinessAddress, string OperatorEmail,
    string SignerName, string SignerTitle, string SignerCapacity,
    string? AcceptedContentSha256, string? UnsignedDocumentSha256, string? SignedDocumentSha256,
    string RowVersion, bool CanPrepare, bool CanSign, DateTimeOffset ApprovedAtUtc,
    DateTimeOffset? SignedAtUtc, string? ErrorCode, ContractOtpPolicyDto OtpPolicy);

public sealed record PrepareOperatorContractInput(string SignerName, string SignerTitle, string SignerCapacity, string RowVersion);
public sealed record OperatorContractChallengeInput(string ExpectedContentSha256, string RowVersion);
public sealed record OperatorContractChallengeDto(Guid ChallengeId, DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ResendAtUtc, string RowVersion, string MaskedEmail);
public sealed record SignOperatorContractInput(string ExpectedContentSha256, string RowVersion,
    string SignerName, bool AcceptedTerms, string ConsentVersion, Guid ChallengeId,
    string Otp, Guid IdempotencyKey, byte[] Signature);
public sealed record OperatorContractDocument(byte[] Bytes, string FileName);