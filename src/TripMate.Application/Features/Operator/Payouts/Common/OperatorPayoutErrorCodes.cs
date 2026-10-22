namespace TripMate.Application.Features.Operator.Payouts.Common;

/// <summary>
/// UC-46 error codes. Display text is owned by the FE (UC-46 FE spec error-code mapping table);
/// these are stable, internal identifiers only. Do not reuse MSG29 (POI coordinates), MSG31-style
/// range codes meant for other features, MSG118/MSG119/MSG120 (unrelated locked meanings), or
/// MSG131-MSG136 (already used by the SRS 5.3 catalogue for account lock/unlock, booking
/// cancellation, and POI outcomes) — see UC-46 BE spec OPEN-4.
/// </summary>
public static class OperatorPayoutErrorCodes
{
    public const string OperatorNotApproved = "operator_payout.operator_not_approved";
    public const string ValidationRequiredField = "operator_payout.validation_required_field";
    public const string InvalidSettlementMonth = "operator_payout.invalid_settlement_month";
    public const string MonthNotClosed = "operator_payout.month_not_closed";
    public const string NoEligibleBooking = "operator_payout.no_eligible_booking";
    public const string PayoutNotRequestable = "operator_payout.payout_not_requestable";
    public const string ActivePayoutExists = "operator_payout.active_payout_exists";
    public const string BelowMinimumPayout = "operator_payout.below_minimum_payout";
    public const string PayoutNotFound = "operator_payout.payout_not_found";
    public const string PayoutNotCancellable = "operator_payout.payout_not_cancellable";
    public const string SystemError = "operator_payout.system_error";
}
