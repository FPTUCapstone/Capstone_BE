namespace TripMate.Application.Features.Admin.Payouts.GetList;

public static class PayoutErrorCodes
{
    public const string Forbidden = "Payouts.Forbidden";

    // Proposed MSG134 wording (D10): the SRS references MSG29 for this case, but the locked
    // MSG29 content describes invalid POI coordinates and must never be displayed here.
    public const string InvalidPeriodRange = "payout.invalid_period_range";

    public const string InvalidPeriodRangeMessage = "The submitted settlement period range is logically invalid.";
}