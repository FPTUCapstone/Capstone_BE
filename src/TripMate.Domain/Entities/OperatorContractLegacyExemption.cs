namespace TripMate.Domain.Entities;

public sealed class OperatorContractLegacyExemption
{
    private OperatorContractLegacyExemption() { }
    public long OperatorUserId { get; private set; }
    public DateTimeOffset GrantedAtUtc { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    public static OperatorContractLegacyExemption Create(long operatorUserId, DateTimeOffset now, string reason)
    {
        if (operatorUserId <= 0) throw new ArgumentOutOfRangeException(nameof(operatorUserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 300) throw new ArgumentOutOfRangeException(nameof(reason));
        return new OperatorContractLegacyExemption { OperatorUserId = operatorUserId, GrantedAtUtc = now.ToUniversalTime(), Reason = reason };
    }
}