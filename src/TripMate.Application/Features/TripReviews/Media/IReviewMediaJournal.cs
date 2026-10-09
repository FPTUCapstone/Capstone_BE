using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripReviews.Media;

public sealed record ReviewMediaVersion(Guid OperationId, byte[] Version);
public static class ReviewMediaErrors
{
    public const string MissingOrForeign = "review_media.missing_or_foreign", InvalidState = "review_media.invalid_state",
        MetadataConflict = "review_media.metadata_conflict", StaleVersion = "review_media.stale_version", TransactionRequired = "review_media.transaction_required";
}
/// <summary>Internal trusted server port, never client upload IDs. Adoption requires the
/// caller's transaction on the scoped context; any failure rolls it back, never commits it.
/// Other methods own their short transactions and reject an existing transaction.</summary>
public interface IReviewMediaJournal
{
    Task<Result<IReadOnlyList<ReviewMediaVersion>>> ReserveAsync(long bookingId, long travelerId, IReadOnlyList<TripReviewMediaOperation> operations, CancellationToken ct);
    Task<Result<IReadOnlyList<ReviewMediaVersion>>> ReserveAsync(ReviewableRecordRef reviewableRecord,
        long travelerId, IReadOnlyList<TripReviewMediaOperation> operations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        return reviewableRecord.IsCommerceBooking
            ? ReserveAsync(reviewableRecord.Id, travelerId, operations, ct)
            : Task.FromException<Result<IReadOnlyList<ReviewMediaVersion>>>(
                new NotSupportedException("This media journal does not support service bookings."));
    }
    Task<Result> AdoptAsync(Guid batchId, long travelerId, IReadOnlyList<ReviewMediaVersion> expected, TripReview parent, DateTimeOffset now, CancellationToken ct);
    Task<Result> MarkCleanupPendingAsync(Guid batchId, long bookingId, long travelerId, IReadOnlyList<ReviewMediaVersion> expected, DateTimeOffset now, CancellationToken ct);
    Task<Result> MarkCleanupPendingAsync(Guid batchId, ReviewableRecordRef reviewableRecord,
        long travelerId, IReadOnlyList<ReviewMediaVersion> expected, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        return reviewableRecord.IsCommerceBooking
            ? MarkCleanupPendingAsync(batchId, reviewableRecord.Id, travelerId, expected, now, ct)
            : Task.FromException<Result>(
                new NotSupportedException("This media journal does not support service bookings."));
    }
}