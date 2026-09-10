using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.OperatorProfiles in database/tripmate_schema_v7.sql.
/// </summary>
public class OperatorProfile
{
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
}
