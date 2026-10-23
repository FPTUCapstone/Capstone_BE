namespace TripMate.Application.Features.Admin.Bookings.Common;

/// <summary>Error codes owned by UC-62, the staff-facing booking register.</summary>
public static class PlatformBookingErrorCodes
{
    public const string Forbidden = "platform_booking.forbidden";
    public const string InvalidFilter = "platform_booking.invalid_filter";
    public const string InvalidDateRange = "platform_booking.invalid_date_range";
    public const string InvalidDateRangeMessage =
        "The submitted booking date range is logically invalid.";
}
