using System.Data;

using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Infrastructure.Persistence;

internal sealed class SqlServerTourMediaCleanupOutboxStore(ApplicationDbContext dbContext)
    : ITourMediaCleanupOutboxStore
{
    public async Task<IReadOnlyList<TourMediaCleanupClaim>> ClaimDueAsync(
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

        List<TourMediaCleanupOutboxItem> dueItems = await dbContext.TourMediaCleanupOutbox
            .FromSqlInterpolated($"""
                SELECT TOP ({batchSize}) *
                FROM commerce.TourMediaCleanupOutbox WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (cleanup_status = 'Pending'
                       AND not_before_at <= {now}
                       AND attempt_count < max_attempts)
                   OR (cleanup_status = 'InProgress'
                       AND lease_expires_at <= {now})
                ORDER BY not_before_at, cleanup_outbox_id
                """)
            .ToListAsync(cancellationToken);

        var claims = new List<TourMediaCleanupClaim>(dueItems.Count);
        foreach (TourMediaCleanupOutboxItem item in dueItems)
        {
            if (item.Status == TourMediaCleanupStatus.InProgress &&
                !item.RecoverExpiredLease(now))
            {
                continue;
            }

            var token = Guid.NewGuid();
            item.BeginAttempt(token, now + leaseDuration, now);
            claims.Add(new TourMediaCleanupClaim(
                item.Id,
                item.CloudinaryPublicId,
                token,
                item.AttemptCount,
                item.MaxAttempts));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claims;
    }

    public Task CompleteAsync(
        long id,
        Guid leaseToken,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            leaseToken,
            item => item.Complete(completedAtUtc),
            cancellationToken);

    public Task RetryAsync(
        long id,
        Guid leaseToken,
        string safeErrorCode,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            leaseToken,
            item => item.ScheduleRetry(safeErrorCode, notBeforeUtc, updatedAtUtc),
            cancellationToken);

    public Task ExhaustAsync(
        long id,
        Guid leaseToken,
        string safeErrorCode,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            leaseToken,
            item => item.Exhaust(safeErrorCode, completedAtUtc),
            cancellationToken);

    private async Task TransitionAsync(
        long id,
        Guid leaseToken,
        Action<TourMediaCleanupOutboxItem> transition,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        TourMediaCleanupOutboxItem? item = await dbContext.TourMediaCleanupOutbox
            .FromSqlInterpolated($"""
                SELECT *
                FROM commerce.TourMediaCleanupOutbox WITH (UPDLOCK, ROWLOCK)
                WHERE cleanup_outbox_id = {id}
                  AND cleanup_status = 'InProgress'
                  AND lease_token = {leaseToken}
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (item is null)
        {
            // A stale worker must not overwrite a newer lease or completed item.
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        transition(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}