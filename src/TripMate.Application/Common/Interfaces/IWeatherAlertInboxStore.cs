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
        CancellationToken cancellationToken);

    /// <summary>
    /// Completes the item only while the caller still holds the lease it was handed
    /// (same lease owner and attempt). Returns false when another worker reclaimed it.
    /// </summary>
    Task<bool> MarkProcessedAsync(WeatherAlertInboxItem leasedItem, CancellationToken cancellationToken);

    /// <summary>Records the failure under the same lease guard as <see cref="MarkProcessedAsync"/>.</summary>
    Task<bool> MarkFailedAsync(WeatherAlertInboxItem leasedItem, string errorMessage, CancellationToken cancellationToken);
}