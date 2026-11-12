using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.Notifications in database/tripmate_schema_v7.sql.
/// </summary>
public class Notification : BaseEntity
{
    public const string TripAlertType = "TripAlert";
    public const string IncidentRelatedEntityType = "Incident";
    public const string TravelGroupHostTransferredType = "TravelGroupHostTransferred";
    public const string TravelGroupMemberRemovedType = "TravelGroupMemberRemoved";
    public const string TravelGroupRelatedEntityType = "TravelGroup";
    public const string EmergencyDepartureCancelledType = "EmergencyDepartureCancelled";
    public const string EmergencyDepartureStoppedType = "EmergencyDepartureStopped";
    public const string BookingRelatedEntityType = "Booking";
    public const string WeatherSafetyReviewCreatedType = "WeatherSafetyReviewCreated";
    public const string WeatherSafetyReviewEscalatedType = "WeatherSafetyReviewEscalated";

    // Shown on the lock screen: never a tour, place, reason, contact, refund or identifier.
    public const string SafetyAlertTitle = "TripMate: cảnh báo an toàn";
    public const string SafetyAlertBody = "Có cảnh báo an toàn của tour cần bạn xem.";
    public const string TripUpdateTitle = "TripMate: cập nhật chuyến đi";
    public const string TripUpdateBody =
        "Tour bạn đã đặt có cập nhật quan trọng. Mở TripMate để xem chi tiết.";

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

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public bool MarkRead(DateTimeOffset readAtUtc)
    {
        if (Status == NotificationStatus.Read)
        {
            return false;
        }

        Status = NotificationStatus.Read;
        ReadAtUtc = readAtUtc.ToUniversalTime();
        return true;
    }
}