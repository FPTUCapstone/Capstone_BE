using System.Data;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Admin.Users.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.Persistence;

internal sealed class SqlServerUserAdministrationNotificationOutboxStore(ApplicationDbContext dbContext)
    : IUserAdministrationNotificationOutboxStore
{
    public async Task<IReadOnlyList<UserAdministrationNotificationClaim>> ClaimDueAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var now = nowUtc.ToUniversalTime();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        List<Notification> dueItems = await dbContext.Notifications
            .FromSqlInterpolated($"""
                SELECT TOP ({batchSize}) notification.*
                FROM dbo.Notifications AS notification WITH (UPDLOCK, READPAST, ROWLOCK)
                INNER JOIN dbo.Users AS [user] ON [user].user_id = notification.user_id
                WHERE notification.status = 'Pending'
                  AND notification.channel = 'Email'
                  AND notification.type = {UserAdministrationNotificationTypes.AccountUnlocked}
                  AND NULLIF(LTRIM(RTRIM([user].email)), '') IS NOT NULL
                  AND (notification.next_delivery_attempt_at IS NULL
                       OR notification.next_delivery_attempt_at <= {now})
                  AND (notification.delivery_lease_expires_at IS NULL
                       OR notification.delivery_lease_expires_at <= {now})
                ORDER BY notification.next_delivery_attempt_at, notification.notification_id
                """)
            .Include(notification => notification.User)
            .ToListAsync(cancellationToken);

        var claims = new List<UserAdministrationNotificationClaim>(dueItems.Count);
        foreach (Notification item in dueItems)
        {
            item.RecoverExpiredDeliveryLease(now);
            var token = Guid.NewGuid();
            item.BeginDeliveryAttempt(token, now + leaseDuration, now);
            claims.Add(new UserAdministrationNotificationClaim(
                item.Id,
                item.User.Email!,
                item.Title ?? UserAdministrationNotificationTypes.AccountUnlockedTitle,
                item.Body ?? UserAdministrationNotificationTypes.AccountUnlockedBody,
                token));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claims;
    }

    public Task CompleteAsync(
        long notificationId,
        Guid leaseToken,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            notificationId,
            leaseToken,
            item => item.CompleteDelivery(leaseToken, completedAtUtc),
            cancellationToken);

    public async Task<bool> RenewLeaseAsync(
        long notificationId,
        Guid leaseToken,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        var now = renewedAtUtc.ToUniversalTime();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        Notification? item = await dbContext.Notifications.FromSqlInterpolated($"""
            SELECT * FROM dbo.Notifications WITH (UPDLOCK, ROWLOCK)
            WHERE notification_id = {notificationId}
              AND status = 'Pending'
              AND delivery_lease_token = {leaseToken}
              AND delivery_lease_expires_at > {now}
            """).SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        item.RenewDeliveryLease(leaseToken, now, leaseExpiresAtUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task RetryAsync(
        long notificationId,
        Guid leaseToken,
        DateTimeOffset notBeforeAtUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            notificationId,
            leaseToken,
            item => item.ScheduleDeliveryRetry(leaseToken, notBeforeAtUtc, updatedAtUtc),
            cancellationToken);

    public Task FailAsync(
        long notificationId,
        Guid leaseToken,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            notificationId,
            leaseToken,
            item => item.FailDelivery(leaseToken, failedAtUtc),
            cancellationToken);

    private async Task TransitionAsync(
        long notificationId,
        Guid leaseToken,
        Action<Notification> transition,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        Notification? item = await dbContext.Notifications
            .FromSqlInterpolated($"""
                SELECT *
                FROM dbo.Notifications WITH (UPDLOCK, ROWLOCK)
                WHERE notification_id = {notificationId}
                  AND status = 'Pending'
                  AND delivery_lease_token = {leaseToken}
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (item is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        transition(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
