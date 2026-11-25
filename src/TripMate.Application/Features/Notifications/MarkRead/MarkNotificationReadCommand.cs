using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Notifications.MarkRead;

public sealed record MarkNotificationReadCommand(long NotificationId, long RecipientUserId) : IRequest<Result>;

public static class NotificationErrorCodes
{
    public const string NotFound = "Notification.NotFound";
}