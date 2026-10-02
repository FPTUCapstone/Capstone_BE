namespace TripMate.Domain.Entities;

/// <summary>Read model for one contributing booking of a payout in payment.PayoutItems (UC-64).</summary>
public sealed class PayoutItem
{
    private PayoutItem()
    {
    }

    public long Id { get; private set; }

    public long PayoutId { get; private set; }

    public Payout Payout { get; private set; } = null!;

    public long BookingId { get; private set; }

    public Booking Booking { get; private set; } = null!;

    public decimal Amount { get; private set; }
}