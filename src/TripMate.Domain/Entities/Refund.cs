namespace TripMate.Domain.Entities;

/// <summary>Read model for a booking refund in payment.Refunds (UC-64).</summary>
public sealed class Refund
{
    public const string StatusPending = "Pending";
    public const string StatusProcessed = "Processed";
    public const string StatusFailed = "Failed";

    private Refund()
    {
    }

    public long Id { get; private set; }

    public long BookingId { get; private set; }

    public Booking Booking { get; private set; } = null!;

    public string? Reason { get; private set; }

    public decimal Amount { get; private set; }

    public string InitiatedBy { get; private set; } = string.Empty;

    public string Status { get; private set; } = StatusPending;

    public DateTimeOffset RequestedAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }
}