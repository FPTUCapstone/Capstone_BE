using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripAlerts.Common;

/// <summary>The frozen UC-70 FCM data values, shared with the Android client.</summary>
public static class EmergencyPushContract
{
    public const string ReviewType = "uc70_weather_safety_review";
    public const string BookingType = "uc70_booking_emergency";
    public const string CancelAction = "CancelBeforeDeparture";
    public const string StopAction = "EmergencyStop";

    // One tray entry per audience: a newer notice replaces the older one, so the tray always
    // opens the latest and Android has fewer entries to bundle under a single tap target.
    public const string ReviewTrayTag = "uc70-review";
    public const string BookingTrayTag = "uc70-booking";
}

/// <summary>
/// Adds UC-70 push notices and their delivery work to the caller's unit of work, so a notice exists
/// exactly when the business change commits. Title and body stay generic: identifiers travel only
/// in the data payload and every detail is read from REST after sign-in.
/// </summary>
public static class EmergencyPushNotices
{
    public static void AddReviewNotice(
        IApplicationDbContext dbContext,
        long operatorUserId,
        long reviewId,
        string type,
        DateTimeOffset nowUtc) =>
        Add(
            dbContext,
            operatorUserId,
            type,
            Notification.SafetyAlertTitle,
            Notification.SafetyAlertBody,
            AuditEntityTypes.WeatherSafetyReview,
            reviewId,
            nowUtc);

    public static void AddBookingNotice(
        IApplicationDbContext dbContext,
        long travelerUserId,
        long bookingId,
        string type,
        DateTimeOffset nowUtc) =>
        Add(
            dbContext,
            travelerUserId,
            type,
            Notification.TripUpdateTitle,
            Notification.TripUpdateBody,
            Notification.BookingRelatedEntityType,
            bookingId,
            nowUtc);

    private static void Add(
        IApplicationDbContext dbContext,
        long userId,
        string type,
        string title,
        string body,
        string relatedEntityType,
        long relatedEntityId,
        DateTimeOffset nowUtc)
    {
        var notification = new Notification
        {
            UserId = userId,
            Channel = NotificationChannel.Push,
            Type = type,
            Title = title,
            Body = body,
            RelatedEntityType = relatedEntityType,
            RelatedEntityId = relatedEntityId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
        };
        dbContext.Notifications.Add(notification);
        dbContext.AlertDeliveryWork.Add(AlertDeliveryWork.Create(notification, nowUtc));
    }
}
