using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.Services;

internal sealed class TourApprovalNoticeDeliveryProcessor(
    ApplicationDbContext dbContext,
    IDateTimeProvider clock,
    ILogger<TourApprovalNoticeDeliveryProcessor> logger)
{
    private const int BatchSize = 50;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    public async Task ProcessDueBatchAsync(CancellationToken cancellationToken)
    {
        var dueAt = clock.UtcNow;
        var notices = await dbContext.TourApprovalNotices.AsNoTracking()
            .Where(notice => notice.NextAttemptAtUtc <= dueAt)
            .OrderBy(notice => notice.NextAttemptAtUtc)
            .ThenBy(notice => notice.TourId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var notice in notices)
        {
            try
            {
                await DeliverAsync(notice, dueAt, cancellationToken);
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException or TimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dbContext.ClearTrackedEntities();
                logger.LogWarning(exception, "Tour approval notice for tour {TourId} will be retried.", notice.TourId);
                var retryAt = clock.UtcNow.Add(RetryDelay);
                await dbContext.TourApprovalNotices
                    .Where(candidate => candidate.TourId == notice.TourId && candidate.NextAttemptAtUtc <= dueAt)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(candidate => candidate.NextAttemptAtUtc, retryAt),
                        cancellationToken);
            }
        }
    }

    private async Task DeliverAsync(TourApprovalNotice notice, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // The delete claims the work under a write lock. The inbox insert and delete commit together,
        // so another worker can neither duplicate a delivery nor lose a notice after a failed write.
        var claimed = await dbContext.TourApprovalNotices
            .Where(candidate => candidate.TourId == notice.TourId && candidate.NextAttemptAtUtc <= dueAt)
            .ExecuteDeleteAsync(cancellationToken);
        if (claimed == 0)
        {
            return;
        }

        dbContext.Notifications.Add(new Notification
        {
            UserId = notice.OperatorUserId,
            Channel = NotificationChannel.InApp,
            Type = Notification.TourApprovedType,
            Title = Notification.TourApprovedTitle,
            Body = $"Your tour package \"{notice.TourTitle}\" was approved.",
            RelatedEntityType = Notification.TourRelatedEntityType,
            RelatedEntityId = notice.TourId,
            CreatedAtUtc = clock.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}