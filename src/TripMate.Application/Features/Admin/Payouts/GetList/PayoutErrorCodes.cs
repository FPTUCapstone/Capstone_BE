namespace TripMate.Application.Features.Admin.Payouts.GetList;

public static class PayoutErrorCodes
{
    public const string Forbidden = "Payouts.Forbidden";
    public const string InvalidStatus = "payout.invalid_status";
    public const string InvalidStatusMessage = "The requested payout status is not available for staff review.";

    // Proposed MSG134 wording (D10): the SRS references MSG29 for this case, but the locked
    // MSG29 content describes invalid POI coordinates and must never be displayed here.
    public const string InvalidPeriodRange = "payout.invalid_period_range";

    public const string InvalidPeriodRangeMessage = "The submitted settlement period range is logically invalid.";

    // UC-64/73/74
    public const string NotFound = "payout.not_found";
    public const string NotFoundMessage = "The requested payout record was not found.";

    // 409: the request is not (or no longer) PendingReview, whether seen before the write or
    // detected as a concurrency conflict during it (I02).
    public const string NotPendingReview = "payout.not_pending_review";
    public const string NotPendingReviewMessage = "The payout request is no longer pending review.";

    // 400: UC-74 rejection reason outside [Payout.RejectionReasonMinLength, RejectionReasonMaxLength]
    // after trim. Maps to MSG157 on the Web (database/migrations/20261014_add_msg157_rejection_reason_length.sql).
    public const string RejectionReasonInvalid = "payout.rejection_reason_invalid";
}