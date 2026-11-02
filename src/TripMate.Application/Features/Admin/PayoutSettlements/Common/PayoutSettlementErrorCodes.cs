namespace TripMate.Application.Features.Admin.PayoutSettlements.Common;

/// <summary>UC-65 errors specific to the Administrator settlement scope. Forbidden, NotFound and the
/// status/filter codes are shared with the Staff review family in
/// <see cref="TripMate.Application.Features.Admin.Payouts.Common.PayoutErrorCodes"/>.</summary>
public static class PayoutSettlementErrorCodes
{
    // 409: the payout is not (or no longer) ApprovedForSettlement, also on a lost concurrency race.
    public const string NotApprovedForSettlement = "payout.not_approved_for_settlement";
    public const string NotApprovedForSettlementMessage = "The payout request is not awaiting settlement authorization.";

    // 422: the stored beneficiary snapshot cannot be sent to a bank.
    public const string BeneficiaryIncomplete = "payout.beneficiary_incomplete";
    public const string BeneficiaryIncompleteMessage = "The payout beneficiary bank details are incomplete.";

    // 422: PayOS takes a positive whole number of VND.
    public const string AmountInvalid = "payout.amount_invalid";
    public const string AmountInvalidMessage = "The payout amount is not a positive whole number of VND.";

    // 422: the beneficiary bank cannot be matched exactly to the standard bank catalogue.
    public const string BankBinUnresolved = "payout.bank_bin_unresolved";
    public const string BankBinUnresolvedMessage = "The beneficiary bank could not be matched to a supported bank.";
}