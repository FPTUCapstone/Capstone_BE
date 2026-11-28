namespace TripMate.Application.Features.Bookings.DirectRefunds.Common;

public static class DirectRefundErrorCodes
{
    public const string ValidationFailed = "direct_refund.validation_failed";
    public const string NotFound = "direct_refund.not_found";
    public const string StorageUnavailable = "direct_refund.storage_unavailable";
    public const string AccessDenied = "direct_refund.access_denied";
    public const string InvalidState = "direct_refund.invalid_state";
    public const string VersionConflict = "direct_refund.version_conflict";
    public const string ResponseWindowClosed = "direct_refund.response_window_closed";
    public const string RecipientRequired = "direct_refund.recipient_required";
    public const string EvidenceInvalid = "direct_refund.evidence_invalid";
    public const string AmountInvalid = "direct_refund.amount_invalid";
    public const string CapExceeded = "direct_refund.cap_exceeded";
    public const string DuplicateSource = "direct_refund.duplicate_source";
    public const string Unauthorized = "direct_refund.unauthorized";
    public const string TransferFutureDate = "direct_refund.transfer_future_date";
    public const string MissingEvidence = "direct_refund.missing_evidence";
    public const string EvidenceNotOwnedByCase = "direct_refund.evidence_not_owned_by_case";
    public const string TooManyEvidences = "direct_refund.too_many_evidences";
}