namespace TripMate.Domain.Enums;

/// <summary>
/// Matches dbo.Notifications.channel after the UC-21 InApp migration.
/// </summary>
public enum NotificationChannel
{
    Push = 1,
    Email = 2,
    SMS = 3,
    InApp = 4,
}