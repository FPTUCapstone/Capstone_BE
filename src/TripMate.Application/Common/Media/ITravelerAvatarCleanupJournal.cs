namespace TripMate.Application.Common.Media;

/// <summary>
/// Persists avatar-cleanup intent independently from the profile update transaction.
/// A reservation created before provider upload remains available for recovery if the
/// request fails or the process stops before the new avatar reference is committed.
/// </summary>
public interface ITravelerAvatarCleanupJournal
{
    /// <summary>Creates or advances durable cleanup work for a Cloudinary avatar public ID.</summary>
    Task ReserveAsync(
        string publicId,
        DateTimeOffset notBeforeAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a pending reservation after the asset is safely represented by the committed
    /// profile transaction, or after cleanup completes.
    /// </summary>
    Task CompleteAsync(string publicId, CancellationToken cancellationToken);

    /// <summary>Schedules existing pending cleanup work to run as soon as possible.</summary>
    Task RetryNowAsync(string publicId, CancellationToken cancellationToken);
}