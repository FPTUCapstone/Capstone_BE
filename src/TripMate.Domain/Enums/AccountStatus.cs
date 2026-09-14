namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the CK constraint on dbo.Users.status in database/tripmate_schema_v7.sql exactly —
/// member names are round-tripped as strings by EF Core, so they must equal the DB values
/// verbatim. PendingApproval/Rejected cover a Tour Operator's account while its application is
/// under review; that state lives on Users.status itself in this schema (not a separate field),
/// but only gates the Tour Operator workspace, never sign-in (BR-07/BR-09).
/// </summary>
public enum AccountStatus
{
    PendingEmailVerification = 1,
    Active = 2,
    Locked = 3,
    PendingApproval = 4,
    Rejected = 5,
    Inactive = 6,
}