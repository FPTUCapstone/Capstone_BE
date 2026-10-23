using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Read model for commercial.ServiceBookings. UC-67 uses the persisted commission amount as the
/// platform-recognized amount for a fulfilled partner service booking.
/// </summary>
public sealed class ServiceBooking : BaseEntity
{
    public long TravelerUserId { get; private set; }
    public long ServiceId { get; private set; }
    public CommercialService Service { get; private set; } = null!;
    public DateTimeOffset StartAtUtc { get; private set; }
    public DateTimeOffset? EndAtUtc { get; private set; }
    public int Quantity { get; private set; }
    public decimal TotalPrice { get; private set; }
    public decimal? CommissionAmount { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
}