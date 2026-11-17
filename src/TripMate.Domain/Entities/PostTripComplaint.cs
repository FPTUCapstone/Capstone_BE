using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class PostTripComplaint : BaseEntity
{
    public const int ReasonDetailMaxLength = 2000;
    public const int RejectionReasonMaxLength = 1000;
    public const int PartialRefundProposalReasonMaxLength = 1000;

    private PostTripComplaint() { }

    public long BookingId { get; private set; }
    public long TravelerUserId { get; private set; }
    public PostTripComplaintStatus Status { get; private set; }
    public PostTripComplaintReasonCategory ReasonCategory { get; private set; }
    public string ReasonDetail { get; private set; } = string.Empty;
    public DateTimeOffset SubmittedAtUtc { get; private set; }
    public DateTimeOffset ResponseDeadlineUtc { get; private set; }
    public DateTimeOffset? TravelerResponseDeadlineUtc { get; private set; }
    public DateTimeOffset? OperatorReconsiderationDeadlineUtc { get; private set; }
    public PostTripComplaintResponseAction? ResponseAction { get; private set; }
    public decimal? ProposedRefundAmount { get; private set; }
    public string? PartialRefundProposalReason { get; private set; }
    public int PartialRefundProposalRound { get; private set; }
    public string? RejectionReason { get; private set; }
    public DateTimeOffset? RespondedAtUtc { get; private set; }
    public long? RespondedByOperatorUserId { get; private set; }
    public decimal RefundableAmount { get; private set; }
    public long? RefundId { get; private set; }
    public DateTimeOffset? EvidenceAmendmentUsedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static PostTripComplaint Create(long bookingId, long travelerUserId,
        PostTripComplaintReasonCategory reasonCategory, string reasonDetail,
        DateTimeOffset submittedAtUtc, DateTimeOffset responseDeadlineUtc,
        decimal refundableAmount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bookingId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonDetail);
        string normalizedReason = reasonDetail.Trim();
        if (normalizedReason.Length > ReasonDetailMaxLength) throw new ArgumentOutOfRangeException(nameof(reasonDetail));
        if (refundableAmount < 0m) throw new ArgumentOutOfRangeException(nameof(refundableAmount));
        DateTimeOffset submittedAt = submittedAtUtc.ToUniversalTime();
        DateTimeOffset deadline = responseDeadlineUtc.ToUniversalTime();
        if (deadline <= submittedAt) throw new ArgumentOutOfRangeException(nameof(responseDeadlineUtc));
        return new PostTripComplaint
        {
            BookingId = bookingId,
            TravelerUserId = travelerUserId,
            Status = PostTripComplaintStatus.AwaitingOperatorResponse,
            ReasonCategory = reasonCategory,
            ReasonDetail = normalizedReason,
            SubmittedAtUtc = submittedAt,
            ResponseDeadlineUtc = deadline,
            RefundableAmount = refundableAmount,
        };
    }

    public bool CanAppendEvidence(DateTimeOffset nowUtc, DateTimeOffset submissionDeadlineUtc) =>
        Status == PostTripComplaintStatus.AwaitingOperatorResponse &&
        EvidenceAmendmentUsedAtUtc is null &&
        nowUtc.ToUniversalTime() <= submissionDeadlineUtc.ToUniversalTime();

    public void MarkEvidenceAmended(DateTimeOffset amendedAtUtc)
    {
        if (EvidenceAmendmentUsedAtUtc is not null)
            throw new InvalidOperationException("Complaint evidence has already been amended.");
        EvidenceAmendmentUsedAtUtc = amendedAtUtc.ToUniversalTime();
    }

    public bool ExpireOperatorResponse(DateTimeOffset nowUtc)
    {
        if (Status != PostTripComplaintStatus.AwaitingOperatorResponse || nowUtc.ToUniversalTime() < ResponseDeadlineUtc)
            return false;
        Status = PostTripComplaintStatus.Expired;
        return true;
    }

    public bool DeclineExpiredTravelerProposal(DateTimeOffset nowUtc)
    {
        if (Status != PostTripComplaintStatus.AwaitingTravelerResponse ||
            TravelerResponseDeadlineUtc is not DateTimeOffset deadline || nowUtc.ToUniversalTime() < deadline)
        {
            return false;
        }
        Status = PostTripComplaintStatus.Declined;
        return true;
    }

    public bool DeclineExpiredOperatorReconsideration(DateTimeOffset nowUtc)
    {
        if (Status != PostTripComplaintStatus.AwaitingOperatorReconsideration ||
            OperatorReconsiderationDeadlineUtc is not DateTimeOffset deadline || nowUtc.ToUniversalTime() < deadline)
        {
            return false;
        }
        Status = PostTripComplaintStatus.Declined;
        return true;
    }

    public bool TryCloseFromTravelerResponse(PostTripComplaintStatus outcome)
    {
        if (Status != PostTripComplaintStatus.AwaitingTravelerResponse)
            return false;
        if (outcome != PostTripComplaintStatus.Approved && outcome != PostTripComplaintStatus.Declined)
            throw new ArgumentOutOfRangeException(nameof(outcome), "Traveler response outcome must be Approved or Declined.");
        Status = outcome;
        return true;
    }

    public void LinkRefund(long refundId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refundId);
        RefundId = refundId;
    }

    public void AcceptFullRefund(long operatorUserId, long refundId, DateTimeOffset respondedAtUtc)
    {
        EnsureOpenForOperatorResponse(respondedAtUtc);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refundId);
        Status = PostTripComplaintStatus.Approved;
        ResponseAction = PostTripComplaintResponseAction.AcceptFullRefund;
        RespondedByOperatorUserId = operatorUserId;
        RespondedAtUtc = respondedAtUtc.ToUniversalTime();
        RefundId = refundId;
    }

    public void ProposePartialRefund(long operatorUserId, decimal amount, string proposalReason,
        DateTimeOffset respondedAtUtc, DateTimeOffset travelerResponseDeadlineUtc)
    {
        bool initialOffer = Status == PostTripComplaintStatus.AwaitingOperatorResponse;
        bool revisedOffer = Status == PostTripComplaintStatus.AwaitingOperatorReconsideration;
        DateTimeOffset now = respondedAtUtc.ToUniversalTime();
        if ((!initialOffer && !revisedOffer) ||
            (initialOffer && now >= ResponseDeadlineUtc) ||
            (revisedOffer && (OperatorReconsiderationDeadlineUtc is not DateTimeOffset reconsiderationDeadline || now >= reconsiderationDeadline)))
        {
            throw new InvalidOperationException("The complaint is no longer open for an operator response.");
        }
        if (initialOffer && PartialRefundProposalRound != 0 || revisedOffer && PartialRefundProposalRound != 1)
            throw new InvalidOperationException("The allowed partial-refund proposal cycle is exhausted.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorUserId);
        if (amount <= 0m || amount > RefundableAmount) throw new ArgumentOutOfRangeException(nameof(amount));
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalReason);
        string normalizedReason = proposalReason.Trim();
        if (normalizedReason.Length > PartialRefundProposalReasonMaxLength)
            throw new ArgumentOutOfRangeException(nameof(proposalReason));
        DateTimeOffset responseDeadline = travelerResponseDeadlineUtc.ToUniversalTime();
        if (responseDeadline <= now)
            throw new ArgumentOutOfRangeException(nameof(travelerResponseDeadlineUtc));
        Status = PostTripComplaintStatus.AwaitingTravelerResponse;
        ResponseAction = PostTripComplaintResponseAction.ProposePartialRefund;
        ProposedRefundAmount = amount;
        PartialRefundProposalReason = normalizedReason;
        PartialRefundProposalRound++;
        TravelerResponseDeadlineUtc = responseDeadline;
        OperatorReconsiderationDeadlineUtc = null;
        RespondedByOperatorUserId = operatorUserId;
        RespondedAtUtc = now;
    }

    public void CounterPartialRefund(long travelerUserId, decimal requestedAmount, string reason,
        DateTimeOffset counteredAtUtc, DateTimeOffset operatorReconsiderationDeadlineUtc)
    {
        if (Status != PostTripComplaintStatus.AwaitingTravelerResponse || PartialRefundProposalRound != 1)
            throw new InvalidOperationException("A counter-offer is no longer available.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        if (requestedAmount <= 0m || requestedAmount > RefundableAmount)
            throw new ArgumentOutOfRangeException(nameof(requestedAmount));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string normalizedReason = reason.Trim();
        if (normalizedReason.Length > RejectionReasonMaxLength) throw new ArgumentOutOfRangeException(nameof(reason));
        DateTimeOffset now = counteredAtUtc.ToUniversalTime();
        DateTimeOffset deadline = operatorReconsiderationDeadlineUtc.ToUniversalTime();
        if (deadline <= now) throw new ArgumentOutOfRangeException(nameof(operatorReconsiderationDeadlineUtc));
        Status = PostTripComplaintStatus.AwaitingOperatorReconsideration;
        OperatorReconsiderationDeadlineUtc = deadline;
    }

    public void DeclineFinalPartialRefund(long travelerUserId, string reason)
    {
        if (Status != PostTripComplaintStatus.AwaitingTravelerResponse || PartialRefundProposalRound != 2)
            throw new InvalidOperationException("A final decline is not available.");
        ArgumentOutOfRangeException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Trim().Length > RejectionReasonMaxLength) throw new ArgumentOutOfRangeException(nameof(reason));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(travelerUserId);
        Status = PostTripComplaintStatus.Declined;
    }

    public void Reject(long operatorUserId, string rejectionReason, DateTimeOffset respondedAtUtc)
    {
        EnsureOpenForOperatorResponse(respondedAtUtc);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operatorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectionReason);
        string normalized = rejectionReason.Trim();
        if (normalized.Length > RejectionReasonMaxLength) throw new ArgumentOutOfRangeException(nameof(rejectionReason));
        Status = PostTripComplaintStatus.Rejected;
        ResponseAction = PostTripComplaintResponseAction.Reject;
        RejectionReason = normalized;
        RespondedByOperatorUserId = operatorUserId;
        RespondedAtUtc = respondedAtUtc.ToUniversalTime();
    }

    private void EnsureOpenForOperatorResponse(DateTimeOffset nowUtc)
    {
        DateTimeOffset now = nowUtc.ToUniversalTime();
        bool initialResponseOpen = Status == PostTripComplaintStatus.AwaitingOperatorResponse && now < ResponseDeadlineUtc;
        bool reconsiderationOpen = Status == PostTripComplaintStatus.AwaitingOperatorReconsideration &&
            OperatorReconsiderationDeadlineUtc is DateTimeOffset reconsiderationDeadline && now < reconsiderationDeadline;
        if (!initialResponseOpen && !reconsiderationOpen)
            throw new InvalidOperationException("The complaint is no longer open for an operator response.");
    }
}
