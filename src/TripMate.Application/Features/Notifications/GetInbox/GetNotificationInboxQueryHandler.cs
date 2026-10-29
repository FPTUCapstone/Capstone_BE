using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Notifications.GetInbox;

public sealed class GetNotificationInboxQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetNotificationInboxQuery, Result<GetNotificationInboxResponse>>
{
    public async Task<Result<GetNotificationInboxResponse>> Handle(
        GetNotificationInboxQuery request,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Notifications.AsNoTracking()
            .Where(item => item.UserId == request.RecipientUserId && item.Channel == NotificationChannel.InApp);
        if (request.UnreadOnly)
        {
            query = query.Where(item => item.Status != NotificationStatus.Read);
        }

        if (request.BeforeId.HasValue)
        {
            query = query.Where(item => item.Id < request.BeforeId.Value);
        }

        var rows = await query.OrderByDescending(item => item.Id)
            .Take(request.Limit + 1)
            .Select(item => new NotificationInboxItem(
                item.Id,
                item.Type,
                item.Title,
                item.Body,
                item.RelatedEntityType,
                item.RelatedEntityId,
                item.CreatedAtUtc,
                item.Status == NotificationStatus.Read))
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > request.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return Result.Success(new GetNotificationInboxResponse(
            rows,
            hasMore ? rows[^1].Id : null));
    }
}