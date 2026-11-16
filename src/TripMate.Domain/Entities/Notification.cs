using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps dbo.Notifications in database/tripmate_schema_v7.sql.
/// </summary>
public class Notification : BaseEntity
{
    public const int DeliveryLastErrorCodeMaxLength = 100;
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
    public const string PostTripComplaintRelatedEntityType = "PostTripComplaint";
    public const string PartialRefundProposalAcceptedType = "PartialRefundProposalAccepted";
    public const string PartialRefundProposalDeclinedType = "PartialRefundProposalDeclined";
    public const string PartialRefundProposalExpiredType = "PartialRefundProposalExpired";

    // UC-70 in-app notices (SRS UC-66). In-app only: they never reach a lock screen.
    public const string EmergencyRefundReviewRequiredType = "EmergencyRefundReviewRequired";
    public const string WeatherWarningType = "WeatherWarning";
    public const string WeatherSafetyReviewAdminEscalatedType = "WeatherSafetyReviewAdminEscalated";
    public const string EmergencyRefundReviewTitle = "Cần xem xét hoàn tiền tour khẩn cấp";
    public const string WeatherWarningTitle = "TripMate: cảnh báo thời tiết";
    public const string WeatherWarningBody =
        "Tour bạn đã đặt có cảnh báo thời tiết. Mở TripMate để xem chi tiết.";
    public const string WeatherSafetyReviewAdminEscalatedTitle = "Cảnh báo thời tiết chưa được xác nhận";

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

    public int DeliveryAttemptCount { get; private set; }

    public Guid? DeliveryLeaseToken { get; private set; }

    public DateTimeOffset? DeliveryLeaseExpiresAtUtc { get; private set; }

    public DateTimeOffset? NextDeliveryAttemptAtUtc { get; private set; }

    public string? DeliveryLastErrorCode { get; private set; }

    public void BeginDeliveryAttempt(
        Guid leaseToken,
        DateTimeOffset leaseExpiresAtUtc,
        DateTimeOffset startedAtUtc)
    {
        if (Status != NotificationStatus.Pending || Channel != NotificationChannel.Email)
        {
            throw new InvalidOperationException("Only pending email notifications can be delivered.");
        }

        if (leaseToken == Guid.Empty)
        {
            throw new ArgumentException("A delivery lease token is required.", nameof(leaseToken));
        }

        var startedAt = startedAtUtc.ToUniversalTime();
        var leaseExpiresAt = leaseExpiresAtUtc.ToUniversalTime();
        if (leaseExpiresAt <= startedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));
        }

        if (NextDeliveryAttemptAtUtc is not null && NextDeliveryAttemptAtUtc > startedAt)
        {
            throw new InvalidOperationException("The notification is not due for delivery.");
        }

        if (DeliveryLeaseExpiresAtUtc is not null && DeliveryLeaseExpiresAtUtc > startedAt)
        {
            throw new InvalidOperationException("The notification is already leased for delivery.");
        }

        DeliveryAttemptCount++;
        DeliveryLeaseToken = leaseToken;
        DeliveryLeaseExpiresAtUtc = leaseExpiresAt;
        NextDeliveryAttemptAtUtc = null;
        DeliveryLastErrorCode = null;
    }

    public void ScheduleDeliveryRetry(
        Guid leaseToken,
        DateTimeOffset notBeforeAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        EnsureCurrentLease(leaseToken);
        var updatedAt = updatedAtUtc.ToUniversalTime();
        var notBeforeAt = notBeforeAtUtc.ToUniversalTime();
        if (notBeforeAt <= updatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(notBeforeAtUtc));
        }

        DeliveryLeaseToken = null;
        DeliveryLeaseExpiresAtUtc = null;
        NextDeliveryAttemptAtUtc = notBeforeAt;
    }

    public void CompleteDelivery(Guid leaseToken, DateTimeOffset completedAtUtc)
    {
        EnsureCurrentLease(leaseToken);
        Status = NotificationStatus.Sent;
        SentAtUtc = completedAtUtc.ToUniversalTime();
        DeliveryLeaseToken = null;
        DeliveryLeaseExpiresAtUtc = null;
        NextDeliveryAttemptAtUtc = null;
        DeliveryLastErrorCode = null;
    }

    public void RenewDeliveryLease(Guid leaseToken, DateTimeOffset renewedAtUtc, DateTimeOffset leaseExpiresAtUtc)
    {
        EnsureCurrentLease(leaseToken);
        var renewedAt = renewedAtUtc.ToUniversalTime();
        var leaseExpiresAt = leaseExpiresAtUtc.ToUniversalTime();
        if (DeliveryLeaseExpiresAtUtc is null || DeliveryLeaseExpiresAtUtc <= renewedAt)
        {
            throw new InvalidOperationException("The notification delivery lease has expired.");
        }

        if (leaseExpiresAt <= renewedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));
        }

        DeliveryLeaseExpiresAtUtc = leaseExpiresAt;
    }

    public void FailDelivery(Guid leaseToken, DateTimeOffset failedAtUtc)
    {
        EnsureCurrentLease(leaseToken);
        Status = NotificationStatus.Failed;
        SentAtUtc = null;
        DeliveryLeaseToken = null;
        DeliveryLeaseExpiresAtUtc = null;
        NextDeliveryAttemptAtUtc = null;
        DeliveryLastErrorCode = "EMAIL_DELIVERY_REJECTED";
    }

    public bool RecoverExpiredDeliveryLease(DateTimeOffset recoveredAtUtc)
    {
        if (Status != NotificationStatus.Pending
            || DeliveryLeaseExpiresAtUtc is null
            || DeliveryLeaseExpiresAtUtc > recoveredAtUtc.ToUniversalTime())
        {
            return false;
        }

        DeliveryLeaseToken = null;
        DeliveryLeaseExpiresAtUtc = null;
        NextDeliveryAttemptAtUtc = recoveredAtUtc.ToUniversalTime();
        return true;
    }

    private void EnsureCurrentLease(Guid leaseToken)
    {
        if (leaseToken == Guid.Empty || DeliveryLeaseToken != leaseToken)
        {
            throw new InvalidOperationException("The notification delivery lease is no longer current.");
        }
    }
}