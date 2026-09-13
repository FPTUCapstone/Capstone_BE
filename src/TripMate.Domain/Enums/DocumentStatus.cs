namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the CK constraint on dbo.OperatorDocuments.status in database/tripmate_schema_v7.sql.
/// Member names are string-converted by EF Core.
/// </summary>
public enum DocumentStatus
{
    Submitted = 1,
    Approved = 2,
    Rejected = 3,
}
