namespace TripMate.Application.Features.Operator.Bookings.Common;

/// <summary>Error codes owned by UC-40, the Tour Operator customer booking views.</summary>
public static class OperatorBookingErrorCodes
{
    public const string InvalidFilter = "operator_booking.invalid_filter";
    public const string InvalidDateRange = "operator_booking.invalid_date_range";
    public const string Forbidden = "operator_booking.forbidden";
    public const string NotFound = "operator_booking.not_found";
}