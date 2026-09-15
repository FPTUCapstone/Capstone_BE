namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the CK constraint on dbo.Notifications.status in database/tripmate_schema_v7.sql.
/// </summary>
public enum NotificationStatus
{
    Pending = 1,
    Sent = 2,
    Failed = 3,
    Read = 4,
}
