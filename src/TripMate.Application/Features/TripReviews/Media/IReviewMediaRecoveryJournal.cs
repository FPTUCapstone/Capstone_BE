using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.Features.TripReviews.Media;

public sealed record ReviewMediaUploadClaim(Guid OperationId, Guid Token, string PublicId);
public sealed record ReviewMediaCleanupClaim(Guid OperationId, Guid Token, string PublicId, bool UploadKnownTerminal);

/// <summary>Every method owns a short SQL transaction. Provider I/O is never performed within it.
/// Upload claims durably record unknown dispatch before returning and never permit a second upload.</summary>
public interface IReviewMediaRecoveryJournal
{
    Task<Result<ReviewMediaUploadClaim>> ClaimUploadAsync(Guid operationId, long travelerId, DateTimeOffset now, CancellationToken ct);
    Task<Result<ReviewMediaVersion>> CompleteUploadAsync(ReviewMediaUploadClaim claim, long travelerId, ReviewMediaStorageResult result, DateTimeOffset now, CancellationToken ct);
    Task<Result> RequestCleanupAsync(Guid batchId, long bookingId, long travelerId, DateTimeOffset now, CancellationToken ct);
    Task<Result> RequestCleanupAsync(Guid batchId, ReviewableRecordRef reviewableRecord,
        long travelerId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reviewableRecord);
        return reviewableRecord.IsCommerceBooking
            ? RequestCleanupAsync(batchId, reviewableRecord.Id, travelerId, now, ct)
            : Task.FromException<Result>(
                new NotSupportedException("This recovery journal does not support service bookings."));
    }
    Task ReconcileAbandonedAsync(DateTimeOffset now, int maximum, CancellationToken ct);
    Task<IReadOnlyList<ReviewMediaCleanupClaim>> ClaimCleanupAsync(DateTimeOffset now, int maximum, CancellationToken ct);
    /// <summary>terminalEvidence is authoritative completion evidence, not repeated absence or lease expiry.
    /// Persisted uncertainty must survive failed attempts, cancellation and exhaustion.</summary>
    Task<Result> CompleteCleanupAsync(ReviewMediaCleanupClaim claim, ReviewMediaStorageOutcome outcome, bool terminalEvidence, DateTimeOffset now, CancellationToken ct);
}