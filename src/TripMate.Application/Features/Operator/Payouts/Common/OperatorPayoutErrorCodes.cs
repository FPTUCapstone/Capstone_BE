namespace TripMate.Application.Features.Operator.Payouts.Common;

/// <summary>
/// Error codes for the Operator beneficiary bank account feature (UC-46's request/cancel/balance
/// codes were removed with the request-based payout flow — see DAILY-PAYOUT-TRANSFER).
/// </summary>
public static class OperatorPayoutErrorCodes
{
    public const string OperatorNotApproved = "operator_payout.operator_not_approved";
    public const string ValidationRequiredField = "operator_payout.validation_required_field";
}