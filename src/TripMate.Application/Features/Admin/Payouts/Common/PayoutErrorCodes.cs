namespace TripMate.Application.Features.Admin.Payouts.Common;

/// <summary>Shared across UC-63 (list), UC-64 (detail), UC-73 (approve) and UC-74 (reject) — the
/// Staff payout review family shares one error-code catalogue the same way
/// Operator.Payouts.Common.OperatorPayoutErrorCodes does for the Operator-facing payout UCs.</summary>
public static class PayoutErrorCodes
{
    public const string Forbidden = "Payouts.Forbidden";

    // UC-63
    public const string InvalidStatus = "payout.invalid_status";
    public const string InvalidStatusMessage = "The requested payout status is not available for staff review.";

    // Proposed MSG134 wording (D10): the SRS references MSG29 for this case, but the locked
    // MSG29 content describes invalid POI coordinates and must never be displayed here.
    public const string InvalidPeriodRange = "payout.invalid_period_range";

    public const string InvalidPeriodRangeMessage = "The submitted settlement period range is logically invalid.";

    // Generic filter shape errors (keyword length, page bounds, out-of-range period values) that
    // have no business-specific code of their own. Status keeps its own InvalidStatus code
    // because the handler, not the validator, owns that check (see GetPayoutsQueryValidator).
    public const string InvalidFilter = "payout.invalid_filter";

    // UC-64/73/74
    public const string NotFound = "payout.not_found";
    public const string NotFoundMessage = "The requested payout record was not found.";

    // 409: the request is not (or no longer) PendingReview, whether seen before the write or
    // detected as a concurrency conflict during it (I02).
    public const string NotPendingReview = "payout.not_pending_review";
    public const string NotPendingReviewMessage = "The payout request is no longer pending review.";

    // 400: UC-74 rejection reason is empty after trim, or exceeds Payout.RejectionReasonMaxLength.
    // SRS UC-74 only requires the reason be present (MSG123); there is no minimum length and no
    // MSG157 in the SRS (decided 2026-10-22, scoped against the SRS text).
    public const string RejectionReasonInvalid = "payout.rejection_reason_invalid";
}
