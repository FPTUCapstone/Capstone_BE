namespace TripMate.Domain.Entities;

/// <summary>One encrypted default recipient per Traveler; refund cases retain independent snapshots.</summary>
public sealed class TravelerRefundAccount
{
    public const int ProtectedValueMaxLength = 2048;
    private TravelerRefundAccount() { }
    public long Id { get; private set; }
    public string BankCode { get; private set; } = string.Empty;
    public string AccountNumberProtected { get; private set; } = string.Empty;
    public string AccountHolderProtected { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static TravelerRefundAccount Create(long userId, string bankCode, string accountNumberProtected,
        string accountHolderProtected, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        var entity = new TravelerRefundAccount { Id = userId };
        entity.Update(bankCode, accountNumberProtected, accountHolderProtected, now);
        return entity;
    }

    public void Update(string bankCode, string accountNumberProtected, string accountHolderProtected, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumberProtected);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountHolderProtected);
        if (bankCode.Length > DirectRefundCase.RecipientBankCodeMaxLength || accountNumberProtected.Length > ProtectedValueMaxLength
            || accountHolderProtected.Length > ProtectedValueMaxLength) throw new ArgumentException("The recipient data exceeds storage bounds.");
        BankCode = bankCode;
        AccountNumberProtected = accountNumberProtected;
        AccountHolderProtected = accountHolderProtected;
        UpdatedAtUtc = now;
    }
}