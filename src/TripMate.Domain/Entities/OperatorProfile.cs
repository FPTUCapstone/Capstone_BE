using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.OperatorProfiles in database/tripmate_schema_v7.sql.
/// </summary>
public class OperatorProfile
{
    public const int RejectionReasonMaxLength = 500;
    public const int BankNameMaxLength = 150;
    public const int AccountHolderNameMaxLength = 150;
    public const int AccountNumberMaskedMaxLength = 30;

    public long UserId { get; set; }

    public User User { get; set; } = null!;

    public string CompanyName { get; set; } = string.Empty;

    public string TaxCode { get; set; } = string.Empty;

    public string BusinessLicenseNo { get; set; } = string.Empty;

    public string? ContactPhone { get; set; }

    public string? ContactAddress { get; set; }

    public decimal CommissionRate { get; set; } = 10.00m;

    public OperatorApprovalStatus ApprovalStatus { get; set; } = OperatorApprovalStatus.PendingApproval;

    public string? RejectionReason { get; set; }

    public long? ReviewedBy { get; set; }

    public User? Reviewer { get; set; }

    public DateTimeOffset? ReviewedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ICollection<OperatorDocument> Documents { get; set; } = new List<OperatorDocument>();

    /// <summary>UC-46 beneficiary bank account. The full account number is never stored in plain
    /// text; only its protected (encrypted) form and a masked display value are kept.</summary>
    public string? BankName { get; set; }

    public string? AccountHolderName { get; set; }

    public string? AccountNumberMasked { get; set; }

    public string? AccountNumberProtected { get; set; }

    public DateTimeOffset? BankAccountUpdatedAtUtc { get; set; }

    public bool HasBeneficiaryBankAccount =>
        !string.IsNullOrWhiteSpace(BankName)
        && !string.IsNullOrWhiteSpace(AccountHolderName)
        && !string.IsNullOrWhiteSpace(AccountNumberProtected);

    /// <summary>Replaces the beneficiary bank account. The caller supplies the already-protected
    /// (encrypted) account number and its masked display form; this entity never decrypts it.</summary>
    public void UpdateBeneficiaryBankAccount(
        string bankName,
        string accountHolderName,
        string accountNumberMasked,
        string accountNumberProtected,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountHolderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumberMasked);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumberProtected);
        if (bankName.Length > BankNameMaxLength || accountHolderName.Length > AccountHolderNameMaxLength
            || accountNumberMasked.Length > AccountNumberMaskedMaxLength)
        {
            throw new ArgumentException("Beneficiary bank account value exceeds its database column length.");
        }

        BankName = bankName.Trim();
        AccountHolderName = accountHolderName.Trim();
        AccountNumberMasked = accountNumberMasked.Trim();
        AccountNumberProtected = accountNumberProtected;
        BankAccountUpdatedAtUtc = nowUtc;
    }
}