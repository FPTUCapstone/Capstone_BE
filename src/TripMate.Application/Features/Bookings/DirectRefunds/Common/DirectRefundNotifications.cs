using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.DirectRefunds.Common;

/// <summary>Builds durable Notification rows for the direct-refund lifecycle (spec §8).</summary>
public static class DirectRefundNotifications
{
    public static Notification TransferReported(long travelerUserId, long caseId, DateTimeOffset deadlineAtUtc, DateTimeOffset nowUtc) =>
        new()
        {
            UserId = travelerUserId,
            Channel = NotificationChannel.Push,
            Type = "DirectRefundTransferReported",
            Title = "Refund transfer reported",
            Body = $"Your operator has reported a direct refund transfer. Please confirm receipt by {deadlineAtUtc:f} UTC.",
            RelatedEntityType = "DirectRefundCase",
            RelatedEntityId = caseId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc,
        };

    public static Notification CaseCompleted(long recipientUserId, long caseId, string method, DateTimeOffset nowUtc) =>
        new()
        {
            UserId = recipientUserId,
            Channel = NotificationChannel.Push,
            Type = "DirectRefundCaseCompleted",
            Title = "Direct refund completed",
            Body = method == DirectRefundCase.MethodAutoCompletedAfter72Hours
                ? "Your direct refund case was automatically completed after the 72-hour confirmation window."
                : method == DirectRefundCase.MethodAdminResolved
                    ? "Your direct refund case was resolved by an administrator."
                    : "Your direct refund transfer has been confirmed.",
            RelatedEntityType = "DirectRefundCase",
            RelatedEntityId = caseId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc,
        };

    public static Notification TransferDeadlineOverdue(long operatorUserId, long caseId, DateTimeOffset nowUtc) =>
        new()
        {
            UserId = operatorUserId,
            Channel = NotificationChannel.Push,
            Type = "DirectRefundTransferOverdue",
            Title = "Direct refund transfer overdue",
            Body = "You have not submitted transfer evidence within the 72-hour window. Please act immediately.",
            RelatedEntityType = "DirectRefundCase",
            RelatedEntityId = caseId,
            Status = NotificationStatus.Pending,
            CreatedAtUtc = nowUtc,
        };
}
