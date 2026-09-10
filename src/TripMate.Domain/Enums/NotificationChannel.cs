namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the CK constraint on dbo.Notifications.channel in database/tripmate_schema_v7.sql.
/// </summary>
public enum NotificationChannel
{
    Push = 1,
    Email = 2,
    SMS = 3,
}
