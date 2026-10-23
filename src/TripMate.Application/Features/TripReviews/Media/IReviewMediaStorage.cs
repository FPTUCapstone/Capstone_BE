namespace TripMate.Application.Features.TripReviews.Media;

public enum ReviewMediaStorageOutcome { Success, AlreadyAbsent, TransientFailure, PermanentFailure, UnknownOutcome }

/// <summary>AlreadyAbsent is an observation, never proof that an earlier upload cannot finish.</summary>
public sealed record ReviewMediaStorageResult(ReviewMediaStorageOutcome Outcome, string? DeliveryUrl = null, long? StoredByteLength = null);

/// <summary>Accepts only persisted, server-generated opaque operation identities. No client paths or URLs.</summary>
public interface IReviewMediaStorage
{
    Task<ReviewMediaStorageResult> UploadAsync(string publicId, InspectedReviewImage image, CancellationToken cancellationToken);
    Task<ReviewMediaStorageResult> ProbeAsync(string publicId, CancellationToken cancellationToken);
    Task<ReviewMediaStorageResult> DestroyAsync(string publicId, CancellationToken cancellationToken);
}