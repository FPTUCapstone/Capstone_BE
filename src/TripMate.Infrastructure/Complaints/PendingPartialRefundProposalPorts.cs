using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Domain.Enums;

namespace TripMate.Infrastructure.Complaints;

/// <summary>
/// Temporary UC-75 binding until TM-222 adds the PostTripComplaint table: no complaint exists yet,
/// so reads return nothing and the deadline job has nothing to close. Replace with the EF-backed
/// store in TM-222.
/// </summary>
internal sealed class PendingPartialRefundProposalStore : IPartialRefundProposalStore
{
    public Task<PartialRefundProposal?> FindAsync(long complaintId, CancellationToken cancellationToken) =>
        Task.FromResult<PartialRefundProposal?>(null);

    public Task<bool> TryCloseAsync(long complaintId, PostTripComplaintStatus outcome, DateTimeOffset respondedAtUtc,
        CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<IReadOnlyList<PartialRefundProposal>> ListOverdueAsync(DateTimeOffset nowUtc, int maxCount,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PartialRefundProposal>>([]);
}

/// <summary>
/// Temporary UC-75 binding until TM-88 adds the UC-42 RefundRequest. It cannot be reached while
/// <see cref="PendingPartialRefundProposalStore"/> finds no complaint. It deliberately fails rather than
/// falling back to the legacy direct-pay Refund, which would break BR-143.
/// </summary>
internal sealed class PendingComplaintRefundRequestCreator : IComplaintRefundRequestCreator
{
    public Task CreateForAcceptedComplaintAsync(long complaintId, long bookingId, decimal amount,
        DateTimeOffset requestedAtUtc, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "UC-42 refund requests are not available yet (TM-88); an accepted complaint cannot be refunded.");
}