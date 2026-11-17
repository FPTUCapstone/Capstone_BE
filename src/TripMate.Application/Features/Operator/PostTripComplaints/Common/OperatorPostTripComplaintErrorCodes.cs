namespace TripMate.Application.Features.Operator.PostTripComplaints.Common;

public static class OperatorPostTripComplaintErrorCodes
{
    public const string Unauthorized = "operator_complaint.unauthorized";
    public const string NotFound = "operator_complaint.not_found";
    public const string Forbidden = "operator_complaint.forbidden";
    public const string ResponseClosed = "operator_complaint.response_closed";
    public const string AmountExceedsRefundable = "operator_complaint.amount_exceeds_refundable";
    public const string RejectionReasonRequired = "operator_complaint.rejection_reason_required";
    public const string ProposalReasonRequired = "operator_complaint.proposal_reason_required";
    public const string IdempotencyMismatch = "operator_complaint.idempotency_key_payload_mismatch";
    public const string RefundUnavailable = "operator_complaint.refund_unavailable";
    public const string InvalidFilter = "operator_complaint.invalid_filter";
}
