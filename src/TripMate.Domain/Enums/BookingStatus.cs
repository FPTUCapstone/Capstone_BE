namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.Bookings.status SQL check constraint.
/// </summary>
public enum BookingStatus
{
    PendingPayment = 1,
    Confirmed = 2,
    Cancelled = 3,
    Completed = 4,
    Expired = 5,
}
