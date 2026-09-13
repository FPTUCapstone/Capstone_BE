using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.Notifications in database/tripmate_schema_v7.sql.
/// </summary>
public class Notification : BaseEntity
{
    public long UserId { get; set; }

    public User User { get; set; } = null!;

    public NotificationChannel Channel { get; set; }

    public string Type { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Body { get; set; }

    public string? RelatedEntityType { get; set; }

    public long? RelatedEntityId { get; set; }

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? SentAtUtc { get; set; }
}
