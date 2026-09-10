namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the CK constraint on dbo.OperatorProfiles.approval_status in database/tripmate_schema_v7.sql.
/// Member names are string-converted by EF Core so they must match the SQL values verbatim.
/// </summary>
public enum OperatorApprovalStatus
{
    PendingApproval = 1,
    Approved = 2,
    Rejected = 3,
}
