using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class OperatorContractSigningChallenge
{
    private OperatorContractSigningChallenge() { }
    public Guid Id { get; private set; }
    public long ContractId { get; private set; }
    public long OperatorUserId { get; private set; }
    public string AcceptedContentSha256 { get; private set; } = string.Empty;
    public string ProtectedOtp { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? SentAtUtc { get; private set; }
    public DateTimeOffset? UsedAtUtc { get; private set; }
    public int FailedAttempts { get; private set; }
    public OperatorContractChallengeStatus Status { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static OperatorContractSigningChallenge Create(Guid id, long contractId, long userId, string acceptedContentSha256,
        string protectedOtp, DateTimeOffset now, TimeSpan ttl)
    {
        if (id == Guid.Empty || contractId <= 0 || userId <= 0 || ttl <= TimeSpan.Zero) throw new ArgumentException("Invalid challenge identity.");
        if (acceptedContentSha256 is null || acceptedContentSha256.Length != 64 || acceptedContentSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A content hash is required.", nameof(acceptedContentSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedOtp);
        if (protectedOtp.Length > 4000) throw new ArgumentOutOfRangeException(nameof(protectedOtp));
        return new OperatorContractSigningChallenge
        {
            Id = id,
            ContractId = contractId,
            OperatorUserId = userId,
            AcceptedContentSha256 = acceptedContentSha256.ToLowerInvariant(),
            ProtectedOtp = protectedOtp,
            CreatedAtUtc = now.ToUniversalTime(),
            ExpiresAtUtc = now.ToUniversalTime().Add(ttl),
            Status = OperatorContractChallengeStatus.Sending
        };
    }
    public bool TryMarkSent(DateTimeOffset now)
    {
        if (Status != OperatorContractChallengeStatus.Sending || now < CreatedAtUtc || now >= ExpiresAtUtc) return false;
        Status = OperatorContractChallengeStatus.Sent; SentAtUtc = now.ToUniversalTime(); return true;
    }
    public bool TryMarkDeliveryFailed(DateTimeOffset now)
    {
        if (Status != OperatorContractChallengeStatus.Sending) return false;
        Status = OperatorContractChallengeStatus.Failed; return true;
    }
    public bool RegisterFailedAttempt(DateTimeOffset now, int maximum = 5)
    {
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (Status != OperatorContractChallengeStatus.Sent || now < CreatedAtUtc || now >= ExpiresAtUtc || FailedAttempts >= maximum) return false;
        FailedAttempts++;
        if (FailedAttempts >= maximum) Status = OperatorContractChallengeStatus.Failed;
        return true;
    }
    public bool TryConsume(DateTimeOffset now)
    {
        if (Status != OperatorContractChallengeStatus.Sent || now < CreatedAtUtc || now >= ExpiresAtUtc) return false;
        Status = OperatorContractChallengeStatus.Used; UsedAtUtc = now.ToUniversalTime(); return true;
    }
}