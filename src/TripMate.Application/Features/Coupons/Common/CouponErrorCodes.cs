namespace TripMate.Application.Features.Coupons.Common;

public static class CouponErrorCodes
{
    public const string OperatorAccessRequired = "coupon.operator_access_required";
    public const string InvalidScope = "coupon.invalid_scope";
    public const string TourNotFound = "coupon.tour_not_found";
    public const string TourNotOwned = "coupon.tour_not_owned";
    public const string TourNotEligible = "coupon.tour_not_eligible";
    public const string CodeConflict = "coupon.code_conflict";
    public const string NotFound = "coupon.not_found";
    public const string InvalidState = "coupon.invalid_state";
    public const string UsageConflict = "coupon.usage_conflict";
}