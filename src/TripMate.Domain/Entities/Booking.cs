namespace TripMate.Domain.Entities;

/// <summary>Read model for a tour booking in commerce.Bookings (UC-64 payout breakdown).</summary>
public sealed class Booking
{
    private Booking()
    {
    }

    public long Id { get; private set; }

    public string BookingCode { get; private set; } = string.Empty;

    public long TravelerUserId { get; private set; }

    public long? TourScheduleId { get; private set; }

    public TourSchedule? TourSchedule { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TotalAmount { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public string PaymentStatus { get; private set; } = string.Empty;

    public DateTimeOffset BookedAtUtc { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public string? CancelReason { get; private set; }
}