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

    // Generic filter shape errors (keyword length, page bounds, out-of-range period values) that
    // have no business-specific code of their own. Status keeps its own InvalidStatus code
    // because the handler, not the validator, owns that check (see GetPayoutsQueryValidator).
    public const string InvalidFilter = "payout.invalid_filter";
}