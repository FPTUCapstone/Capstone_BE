using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Infrastructure.Weather;

public sealed class SqlWeatherAlertInboxStore(
    IApplicationDbContext context,
    TimeProvider timeProvider,
    ILogger<SqlWeatherAlertInboxStore> logger) : IWeatherAlertInboxStore
{
    public async Task<IReadOnlyList<WeatherAlertInboxItem>> LeasePendingItemsAsync(
        int batchSize,
        TimeSpan leaseDuration,
        string leasedBy,
        CancellationToken cancellationToken)
    {
        var utcNow = timeProvider.GetUtcNow();
        var leaseExpiry = utcNow.Add(leaseDuration);

        try
        {
            return await LeaseAsync(batchSize, leasedBy, utcNow, leaseExpiry, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker claimed one of these rows first; this worker leases again next poll.
            context.ClearTrackedEntities();
            logger.LogDebug("Inbox lease for worker {WorkerId} lost a race; retrying on the next poll.", leasedBy);
            return [];
        }
    }

    private Task<IReadOnlyList<WeatherAlertInboxItem>> LeaseAsync(
        int batchSize,
        string leasedBy,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiry,
        CancellationToken cancellationToken) =>
        context.ExecuteInTransactionAsync(async ct =>
        {
            var candidates = await context.WeatherAlertInboxItems
                .Where(x => (x.Status == WeatherAlertInboxItem.StatusPending ||
                            (x.Status == WeatherAlertInboxItem.StatusProcessing && x.LeaseExpiresAtUtc < utcNow))
                            && x.AttemptCount < x.MaxAttempts)
                .OrderBy(x => x.ReceivedAtUtc)
                .Take(batchSize)
                .ToListAsync(ct);

            foreach (var item in candidates)
            {
                item.AcquireLease(leasedBy, leaseExpiry);
            }

            if (candidates.Count > 0)
            {
                await context.SaveChangesAsync(ct);
                logger.LogDebug("Leased {Count} inbox items for worker {WorkerId} until {Expiry}",
                    candidates.Count, leasedBy, leaseExpiry);
            }

            return (IReadOnlyList<WeatherAlertInboxItem>)candidates;
        }, cancellationToken);

    public async Task<bool> EnqueueIfNotExistsAsync(
        string provider,
        string providerNotificationId,
        string payload,
        CancellationToken cancellationToken)
    {
        var exists = await context.WeatherAlertInboxItems
            .AsNoTracking()
            .AnyAsync(x => x.Provider == provider && x.ProviderNotificationId == providerNotificationId, cancellationToken);

        if (exists)
        {
            logger.LogDebug("Notification {NotificationId} from {Provider} already exists in inbox. Skipping.", providerNotificationId, provider);
            return false;
        }

        var item = WeatherAlertInboxItem.CreatePending(
            provider: provider,
            payload: payload,
            providerNotificationId: providerNotificationId,
            receivedAtUtc: timeProvider.GetUtcNow());

        context.WeatherAlertInboxItems.Add(item);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Enqueued notification {NotificationId} from {Provider} into inbox item {InboxId}.",
                providerNotificationId, provider, item.Id);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Concurrent duplicate delivery detected for notification {NotificationId} from {Provider}.", providerNotificationId, provider);
            return false;
        }
    }

    public async Task<bool> MarkProcessedAsync(WeatherAlertInboxItem leasedItem, CancellationToken cancellationToken)
    {
        var processedAt = timeProvider.GetUtcNow();
        int updated = await StillLeased(leasedItem).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, WeatherAlertInboxItem.StatusProcessed)
            .SetProperty(item => item.ProcessedAtUtc, processedAt)
            .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null)
            .SetProperty(item => item.LeasedBy, (string?)null), cancellationToken);

        return updated == 1;
    }

    public async Task<bool> MarkFailedAsync(
        WeatherAlertInboxItem leasedItem,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        string error = errorMessage.Length > WeatherAlertInboxItem.LastErrorMaxLength
            ? errorMessage[..WeatherAlertInboxItem.LastErrorMaxLength]
            : errorMessage;
        int updated = await StillLeased(leasedItem).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, item => item.AttemptCount >= item.MaxAttempts
                ? WeatherAlertInboxItem.StatusFailed
                : WeatherAlertInboxItem.StatusPending)
            .SetProperty(item => item.LastError, error)
            .SetProperty(item => item.LeaseExpiresAtUtc, (DateTimeOffset?)null)
            .SetProperty(item => item.LeasedBy, (string?)null), cancellationToken);

        return updated == 1;
    }

    // The lease owner plus the attempt number fence a lease: a reclaim by any worker, even
    // the same one after its lease expired, raises the attempt count.
    private IQueryable<WeatherAlertInboxItem> StillLeased(WeatherAlertInboxItem leasedItem)
    {
        long id = leasedItem.Id;
        string? leasedBy = leasedItem.LeasedBy;
        int attempt = leasedItem.AttemptCount;
        return context.WeatherAlertInboxItems.Where(item => item.Id == id
            && item.Status == WeatherAlertInboxItem.StatusProcessing
            && item.LeasedBy == leasedBy
            && item.AttemptCount == attempt);
    }
}