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

    // UC-73 [Retry Transfer] inherits UC-74's preconditions (SRS 3.10.6 Normal Flow: "(UC-74 rules
    // apply)") — a retry must not be sent while either still holds, matching BR-160/MSG146.
    public const string RetryBlockedOperatorLocked = "daily_payout.retry_blocked_operator_locked";
    public const string RetryBlockedOperatorLockedMessage = "This Tour Operator account is locked; the transfer cannot be retried until it is unlocked."; // BR-160

    public const string RetryBlockedMissingBankAccount = "daily_payout.retry_blocked_missing_bank_account";
    public const string RetryBlockedMissingBankAccountMessage = "This Tour Operator has no beneficiary bank account on file; the transfer cannot be retried until one is added."; // MSG146
}