namespace TripMate.Application.Features.Traveler.Bookings.Common;

public static class TourBookingErrorCodes
{
    public const string AccessRequired = "tour_booking.access_required";
    public const string ScheduleNotFound = "tour_booking.schedule_not_found";
    public const string ScheduleNotOpen = "tour_booking.schedule_not_open";
    public const string InsufficientCapacity = "tour_booking.insufficient_capacity";
    public const string VoucherNotFound = "tour_booking.voucher_not_found";
    public const string VoucherNotEligible = "tour_booking.voucher_not_eligible";
    public const string VoucherMinimumNotMet = "tour_booking.voucher_minimum_not_met";
    public const string VoucherUsageLimitReached = "tour_booking.voucher_usage_limit_reached";
    public const string ParticipantsRequired = "tour_booking.participants_required";
    public const string IdempotencyKeyRequired = "tour_booking.idempotency_key_required";
    public const string IdempotencyKeyPayloadMismatch = "tour_booking.idempotency_key_payload_mismatch";
    public const string TooManyUnpaidBookings = "tour_booking.too_many_unpaid_bookings";
}