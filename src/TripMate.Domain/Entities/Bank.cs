namespace TripMate.Domain.Entities;

/// <summary>
/// Standard NAPAS/VietQR bank catalogue (UC-65). A beneficiary bank is accepted for a transfer
/// only when it matches a row here exactly; the catalogue is seeded by a reviewed SQL script.
/// </summary>
public sealed class Bank
{
    public const int ShortNameMaxLength = 50;
    public const int OfficialNameMaxLength = 200;

    private Bank()
    {
    }

    public Bank(string bankBin, string shortName, string officialName, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(bankBin)
            || bankBin.Length != Payout.BankBinLength
            || !bankBin.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A bank BIN is exactly six digits.", nameof(bankBin));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(shortName);
        ArgumentException.ThrowIfNullOrWhiteSpace(officialName);
        BankBin = bankBin;
        ShortName = shortName;
        OfficialName = officialName;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public string BankBin { get; private set; } = string.Empty;

    public string ShortName { get; private set; } = string.Empty;

    public string OfficialName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
