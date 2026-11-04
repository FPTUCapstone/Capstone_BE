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

        return await context.ExecuteInTransactionAsync(async ct =>
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
    }

    public async Task<bool> EnqueueIfNotExistsAsync(
        string provider,
        string providerNotificationId,
        string payload,
        string? signature,
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
            signature: signature,
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

    public async Task MarkProcessedAsync(long inboxId, CancellationToken cancellationToken)
    {
        var item = await context.WeatherAlertInboxItems.FindAsync([inboxId], cancellationToken);
        if (item is null) return;

        item.MarkProcessed(timeProvider.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(long inboxId, string errorMessage, CancellationToken cancellationToken)
    {
        var item = await context.WeatherAlertInboxItems.FindAsync([inboxId], cancellationToken);
        if (item is null) return;

        item.MarkFailed(errorMessage);
        await context.SaveChangesAsync(cancellationToken);
    }
}
