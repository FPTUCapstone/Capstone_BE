using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripAlerts.Common;

/// <summary>
/// Adds one in-app notice per active Administrator to the caller's unit of work, so the notice
/// exists exactly when the business change commits. In-app only: it never reaches a lock screen.
/// </summary>
public static class AdministratorInAppNotices
{
    public static async Task<int> AddAsync(
        IApplicationDbContext dbContext,
        string type,
        string title,
        string body,
        string relatedEntityType,
        long relatedEntityId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        List<long> administratorIds = await dbContext.Users.AsNoTracking()
            .Where(user => user.Role == UserRole.Administrator && user.Status == AccountStatus.Active)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
        foreach (long administratorId in administratorIds)
        {
            dbContext.Notifications.Add(new Notification
            {
                UserId = administratorId,
                Channel = NotificationChannel.InApp,
                Type = type,
                Title = title,
                Body = body,
                RelatedEntityType = relatedEntityType,
                RelatedEntityId = relatedEntityId,
                Status = NotificationStatus.Pending,
                CreatedAtUtc = nowUtc.ToUniversalTime(),
            });
        }

        return administratorIds.Count;
    }
}