using MediatR;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Complaints.PartialRefundProposals;

public enum PartialRefundDecision
{
    Accept = 1,
    CounterOffer = 2,
    Decline = 3,
}

/// <summary>UC-75 Respond to Partial Refund Proposal (SRS §3.8.6.3).</summary>
public sealed record RespondToPartialRefundProposalCommand(long ComplaintId, PartialRefundDecision Decision,
    string? Reason = null, decimal? RequestedAmount = null)
    : IRequest<Result<RespondToPartialRefundProposalResponse>>;

public sealed record RespondToPartialRefundProposalResponse(
    long ComplaintId,
    string Status,
    decimal? ApprovedAmount,
    DateTimeOffset RespondedAtUtc,
    decimal? CounterOfferAmount = null);

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
            bool counterOffer = request.Decision == PartialRefundDecision.CounterOffer;
            bool finalDecline = request.Decision == PartialRefundDecision.Decline;
            if (!accepted && !counterOffer && !finalDecline)
                return Result.Failure<RespondToPartialRefundProposalResponse>(PartialRefundProposalErrorCodes.ResponseClosed,
                    "The response decision is invalid.");
            if ((counterOffer || finalDecline) && (string.IsNullOrWhiteSpace(request.Reason) ||
                request.Reason.Trim().Length > TripMate.Domain.Entities.PostTripComplaint.RejectionReasonMaxLength))
                return Result.Failure<RespondToPartialRefundProposalResponse>(PartialRefundProposalErrorCodes.ReasonRequired,
                    "A reason is required when declining a partial-refund proposal.");

            if (counterOffer)
            {
                if (proposal.Round != 1 || request.RequestedAmount is not > 0m ||
                    request.RequestedAmount > proposal.RefundableAmount)
                    return Result.Failure<RespondToPartialRefundProposalResponse>(PartialRefundProposalErrorCodes.CounterAmountInvalid,
                        "The requested amount must be greater than zero and no greater than the refundable amount.");
                if (!await store.TryCounterAsync(proposal.ComplaintId, travelerId, request.RequestedAmount.Value,
                    request.Reason!, nowUtc, nowUtc.AddDays(3), transactionCancellationToken))
                    return PartialRefundProposalAccess.ResponseClosed<RespondToPartialRefundProposalResponse>();
                dbContext.PostTripComplaintNegotiationEvents.Add(TripMate.Domain.Entities.PostTripComplaintNegotiationEvent.Create(
                    proposal.ComplaintId, proposal.Round, travelerId, TripMate.Domain.Enums.PostTripComplaintNegotiationActor.Traveler,
                    TripMate.Domain.Enums.PostTripComplaintNegotiationEventType.CounterOffer, request.RequestedAmount, request.Reason, nowUtc));
                dbContext.Notifications.Add(PartialRefundProposalNotifications.CounterOffered(proposal, request.RequestedAmount.Value, nowUtc));
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return Result.Success(new RespondToPartialRefundProposalResponse(proposal.ComplaintId,
                    PostTripComplaintStatus.AwaitingOperatorReconsideration.ToString(), null, nowUtc, request.RequestedAmount));
            }

            if (finalDecline && proposal.Round != 2)
                return PartialRefundProposalAccess.ResponseClosed<RespondToPartialRefundProposalResponse>();

            var outcome = accepted ? PostTripComplaintStatus.Approved : PostTripComplaintStatus.Declined;
            bool transitioned = accepted
                ? await store.TryCloseAsync(proposal.ComplaintId, outcome, nowUtc, transactionCancellationToken)
                : await store.TryDeclineFinallyAsync(proposal.ComplaintId, travelerId, request.Reason!, nowUtc, transactionCancellationToken);
            if (!transitioned) return PartialRefundProposalAccess.ResponseClosed<RespondToPartialRefundProposalResponse>();

            if (accepted)
            {
                // BR-143: refunded under UC-67 only after the Tour Operator confirms under UC-42.
                await refundRequests.CreateForAcceptedComplaintAsync(proposal.ComplaintId, proposal.BookingId,
                    proposal.ProposedAmount, nowUtc, transactionCancellationToken);
            }

            dbContext.PostTripComplaintNegotiationEvents.Add(TripMate.Domain.Entities.PostTripComplaintNegotiationEvent.Create(
                proposal.ComplaintId, proposal.Round, travelerId, TripMate.Domain.Enums.PostTripComplaintNegotiationActor.Traveler,
                accepted ? TripMate.Domain.Enums.PostTripComplaintNegotiationEventType.Accepted : TripMate.Domain.Enums.PostTripComplaintNegotiationEventType.Declined,
                accepted ? proposal.ProposedAmount : null, request.Reason, nowUtc));
            dbContext.Notifications.Add(accepted ? PartialRefundProposalNotifications.Accepted(proposal, nowUtc)
                : PartialRefundProposalNotifications.Declined(proposal, nowUtc));
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(new RespondToPartialRefundProposalResponse(proposal.ComplaintId, outcome.ToString(),
                accepted ? proposal.ProposedAmount : null, nowUtc));
        }, cancellationToken);
    }
}