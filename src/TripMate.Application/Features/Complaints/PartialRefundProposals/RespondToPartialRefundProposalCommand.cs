using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.PartialRefundProposals;

public enum PartialRefundDecision
{
    Accept = 1,
    Decline = 2,
}

/// <summary>UC-75 Respond to Partial Refund Proposal (SRS §3.8.6.3).</summary>
public sealed record RespondToPartialRefundProposalCommand(long ComplaintId, PartialRefundDecision Decision)
    : IRequest<Result<RespondToPartialRefundProposalResponse>>;

public sealed record RespondToPartialRefundProposalResponse(
    long ComplaintId,
    string Status,
    decimal? ApprovedAmount,
    DateTimeOffset RespondedAtUtc);

public sealed class RespondToPartialRefundProposalCommandHandler(
    IApplicationDbContext dbContext,
    IPartialRefundProposalStore store,
    IComplaintRefundRequestCreator refundRequests,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<RespondToPartialRefundProposalCommand, Result<RespondToPartialRefundProposalResponse>>
{
    public async Task<Result<RespondToPartialRefundProposalResponse>> Handle(
        RespondToPartialRefundProposalCommand request, CancellationToken cancellationToken)
    {
        if (!PartialRefundProposalAccess.TryGetTraveler(currentUser, out long travelerId))
            return PartialRefundProposalAccess.Unauthorized<RespondToPartialRefundProposalResponse>();

        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            var proposal = await store.FindAsync(request.ComplaintId, transactionCancellationToken);
            if (proposal is null) return PartialRefundProposalAccess.NotFound<RespondToPartialRefundProposalResponse>();
            if (proposal.TravelerUserId != travelerId)
                return PartialRefundProposalAccess.Forbidden<RespondToPartialRefundProposalResponse>();

            var nowUtc = clock.UtcNow;
            // G1 + G5: an answered proposal, or one past its deadline before the job ran, is closed.
            if (!PartialRefundProposalRules.IsOpen(proposal.Status, proposal.ResponseDeadlineUtc, nowUtc))
                return PartialRefundProposalAccess.ResponseClosed<RespondToPartialRefundProposalResponse>();

            bool accepted = request.Decision == PartialRefundDecision.Accept;
            var outcome = accepted ? PostTripComplaintStatus.Approved : PostTripComplaintStatus.Declined;
            if (!await store.TryCloseAsync(proposal.ComplaintId, outcome, nowUtc, transactionCancellationToken))
                return PartialRefundProposalAccess.ResponseClosed<RespondToPartialRefundProposalResponse>();

            if (accepted)
            {
                // BR-143: refunded under UC-67 only after the Tour Operator confirms under UC-42.
                await refundRequests.CreateForAcceptedComplaintAsync(proposal.ComplaintId, proposal.BookingId,
                    proposal.ProposedAmount, nowUtc, transactionCancellationToken);
            }

            dbContext.Notifications.Add(accepted
                ? PartialRefundProposalNotifications.Accepted(proposal, nowUtc)
                : PartialRefundProposalNotifications.Declined(proposal, nowUtc));
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(new RespondToPartialRefundProposalResponse(proposal.ComplaintId, outcome.ToString(),
                accepted ? proposal.ProposedAmount : null, nowUtc));
        }, cancellationToken);
    }
}