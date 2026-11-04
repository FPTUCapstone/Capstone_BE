using TripMate.Domain.Entities;

namespace TripMate.Application.Common.Interfaces;

public interface IWeatherAlertInboxStore
{
    Task<IReadOnlyList<WeatherAlertInboxItem>> LeasePendingItemsAsync(
        int batchSize,
        TimeSpan leaseDuration,
        string leasedBy,
        CancellationToken cancellationToken);

    Task<bool> EnqueueIfNotExistsAsync(
        string provider,
        string providerNotificationId,
        string payload,
        string? signature,
        CancellationToken cancellationToken);

    Task MarkProcessedAsync(long inboxId, CancellationToken cancellationToken);

    Task MarkFailedAsync(long inboxId, string errorMessage, CancellationToken cancellationToken);
}
