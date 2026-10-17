namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the commerce.Bookings.payment_status SQL check constraint.
/// </summary>
public enum PaymentStatus
{
    Unpaid = 1,
    Paid = 2,
    Refunded = 3,
    PartiallyRefunded = 4,
}
