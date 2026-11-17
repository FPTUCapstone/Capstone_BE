using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.PartialRefundProposals;

/// <summary>The part of a PostTripComplaint that UC-75 reads (SRS entity #34).</summary>
public sealed record PartialRefundProposal(
    long ComplaintId,
    long BookingId,
    string BookingCode,
    long TravelerUserId,
    long TourOperatorUserId,
    PostTripComplaintStatus Status,
    decimal ProposedAmount,
    DateTimeOffset ResponseDeadlineUtc,
    string? ProposalReason = null,
    int Round = 1,
    DateTimeOffset? OperatorReconsiderationDeadlineUtc = null,
    decimal RefundableAmount = 0m);

/// <summary>
/// Port to the PostTripComplaint aggregate owned by UC-68/UC-69 (TM-222). Implementations run on
/// the caller's <c>IApplicationDbContext</c> so the status change commits with the refund request
/// and the notifications.
/// </summary>
public interface IPartialRefundProposalStore
{
    Task<PartialRefundProposal?> FindAsync(long complaintId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the complaint from AwaitingTravelerResponse to <paramref name="outcome"/> only if it is
    /// still AwaitingTravelerResponse. Returns false when another writer closed it first.
    /// </summary>
    Task<bool> TryCloseAsync(long complaintId, PostTripComplaintStatus outcome, DateTimeOffset respondedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> TryCounterAsync(long complaintId, long travelerUserId, decimal requestedAmount, string reason,
        DateTimeOffset counteredAtUtc, DateTimeOffset operatorReconsiderationDeadlineUtc,
        CancellationToken cancellationToken);

    Task<bool> TryDeclineFinallyAsync(long complaintId, long travelerUserId, string reason,
        DateTimeOffset declinedAtUtc, CancellationToken cancellationToken);

    /// <summary>Proposals still AwaitingTravelerResponse whose deadline is at or before <paramref name="nowUtc"/>.</summary>
    Task<IReadOnlyList<PartialRefundProposal>> ListOverdueAsync(DateTimeOffset nowUtc, int maxCount,
        CancellationToken cancellationToken);
}

/// <summary>
/// Port to the UC-42 refund request (TM-88). An accepted complaint is refunded only after the Tour
/// Operator confirms it under UC-42 (BR-143); UC-75 never pays the Traveler directly.
/// </summary>
public interface IComplaintRefundRequestCreator
{
    Task CreateForAcceptedComplaintAsync(long complaintId, long bookingId, decimal amount,
        DateTimeOffset requestedAtUtc, CancellationToken cancellationToken);
}