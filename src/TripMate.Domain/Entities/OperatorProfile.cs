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
    public const int CompanyNameMaxLength = 200;
    public const int ContactPhoneMaxLength = 20;
    public const int ContactAddressMaxLength = 300;
    public const int DescriptionMaxLength = 2000;
    public const int ContactEmailMaxLength = 200;
    public const int WebsiteUrlMaxLength = 300;
    public const int LogoUrlMaxLength = 500;
    public const int LogoPublicIdMaxLength = 300;

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

    /// <summary>UC-34 editable business profile. Written only through <see cref="UpdateBusinessProfile"/>,
    /// <see cref="SetLogo"/> and <see cref="ClearLogo"/>.</summary>
    public string? Description { get; private set; }

    public string? ContactEmail { get; private set; }

    public string? WebsiteUrl { get; private set; }

    public string? LogoUrl { get; private set; }

    public string? LogoPublicId { get; private set; }

    /// <summary>Replaces the editable business profile. Blank optional values become null. Legal
    /// identifiers, approval state, commission and the bank account are never changed here.</summary>
    public void UpdateBusinessProfile(
        string companyName,
        string? description,
        string? contactAddress,
        string? contactPhone,
        string? contactEmail,
        string? websiteUrl,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        string name = companyName.Trim();
        string? normalizedDescription = NormalizeOptional(description, DescriptionMaxLength, nameof(description));
        string? normalizedAddress = NormalizeOptional(contactAddress, ContactAddressMaxLength, nameof(contactAddress));
        string? normalizedPhone = NormalizeOptional(contactPhone, ContactPhoneMaxLength, nameof(contactPhone));
        string? normalizedEmail = NormalizeOptional(contactEmail, ContactEmailMaxLength, nameof(contactEmail));
        string? normalizedWebsite = NormalizeOptional(websiteUrl, WebsiteUrlMaxLength, nameof(websiteUrl));
        if (name.Length > CompanyNameMaxLength)
        {
            throw new ArgumentException("Company name exceeds its database column length.", nameof(companyName));
        }

        CompanyName = name;
        Description = normalizedDescription;
        ContactAddress = normalizedAddress;
        ContactPhone = normalizedPhone;
        ContactEmail = normalizedEmail;
        WebsiteUrl = normalizedWebsite;
        UpdatedAtUtc = nowUtc;
    }

    public void SetLogo(string logoUrl, string logoPublicId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logoUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(logoPublicId);
        if (logoUrl.Length > LogoUrlMaxLength || logoPublicId.Length > LogoPublicIdMaxLength)
        {
            throw new ArgumentException("Logo value exceeds its database column length.");
        }

        LogoUrl = logoUrl;
        LogoPublicId = logoPublicId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Removes the logo. Does nothing (and keeps <see cref="UpdatedAtUtc"/>) when no logo exists.</summary>
    public void ClearLogo(DateTimeOffset nowUtc)
    {
        if (LogoUrl is null && LogoPublicId is null)
        {
            return;
        }

        LogoUrl = null;
        LogoPublicId = null;
        UpdatedAtUtc = nowUtc;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new ArgumentException("Value exceeds its database column length.", parameterName)
            : trimmed;
    }

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