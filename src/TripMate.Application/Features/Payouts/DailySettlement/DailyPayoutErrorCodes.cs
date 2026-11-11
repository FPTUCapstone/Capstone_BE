namespace TripMate.Application.Features.Payouts.DailySettlement;

/// <summary>Error codes for the DAILY-PAYOUT-TRANSFER feature's Administrator actions (UC-73).</summary>
public static class DailyPayoutErrorCodes
{
    public const string Forbidden = "daily_payout.forbidden";
    public const string ForbiddenMessage = "You do not have permission to access this function.";

    public const string BatchNotFound = "daily_payout.batch_not_found";
    public const string BatchNotFoundMessage = "The requested payout settlement could not be found.";

    public const string NotFailed = "daily_payout.not_failed";
    public const string NotFailedMessage = "This payout settlement is not in SettlementFailed status.";

    public const string ManualReferenceRequired = "daily_payout.manual_reference_required";
    public const string ManualReferenceRequiredMessage = "A transfer reference is required to record a manual transfer.";
}