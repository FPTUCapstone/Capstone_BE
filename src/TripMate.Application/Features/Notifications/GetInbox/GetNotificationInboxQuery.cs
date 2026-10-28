using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Notifications.GetInbox;

public sealed record GetNotificationInboxQuery(
    long RecipientUserId,
    bool UnreadOnly = false,
    int Limit = 20,
    long? BeforeId = null) : IRequest<Result<GetNotificationInboxResponse>>;

public sealed record NotificationInboxItem(
    long Id,
    string Type,
    string? Title,
    string? Body,
    string? RelatedEntityType,
    long? RelatedEntityId,
    DateTimeOffset CreatedAtUtc,
    bool IsRead);

public sealed record GetNotificationInboxResponse(
    IReadOnlyList<NotificationInboxItem> Items,
    long? NextBeforeId);