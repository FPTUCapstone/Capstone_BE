namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>Error codes for the DAILY-PAYOUT-TRANSFER feature's Administrator actions (UC-73).</summary>
public static class DailyPayoutErrorCodes
{
    public const string Forbidden = "daily_payout.forbidden";
    public const string ForbiddenMessage = "You do not have permission to access this function.";

    public const string BatchNotFound = "daily_payout.batch_not_found";
    public const string BatchNotFoundMessage = "The requested payout settlement could not be found.";

    public const string NotFailed = "daily_payout.not_failed";
    public const string NotFailedMessage = "This payout settlement is not in SettlementFailed status."; // MSG177

    public const string ResolutionReasonRequired = "daily_payout.resolution_reason_required";
    public const string ResolutionReasonRequiredMessage = "A reason is required to mark this payout settlement as resolved."; // MSG178

    public const string OperatorHasOutstandingDebt = "daily_payout.operator_has_outstanding_debt";
    public const string OperatorHasOutstandingDebtMessage = "This Tour Operator still has unrecovered pending debt and cannot be unlocked."; // MSG199

    public const string OperatorNotFound = "daily_payout.operator_not_found";
    public const string OperatorNotFoundMessage = "The requested Tour Operator could not be found.";

    public const string OperatorNotLocked = "daily_payout.operator_not_locked";
    public const string OperatorNotLockedMessage = "This Tour Operator is not currently locked.";
}