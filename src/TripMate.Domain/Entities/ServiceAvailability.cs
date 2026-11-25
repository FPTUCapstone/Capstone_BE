namespace TripMate.Domain.Entities;

public sealed class ServiceAvailability
{
    private ServiceAvailability() { }

    public ServiceAvailability(long serviceId, DateTimeOffset startsAt, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serviceId);
        ServiceId = serviceId;
        StartsAtUtc = startsAt.ToUniversalTime();
        SetQuantity(quantity);
    }

    public long ServiceId { get; private set; }
    public DateTimeOffset StartsAtUtc { get; private set; }
    public int Quantity { get; private set; }

    public void SetQuantity(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        Quantity = quantity;
    }
}
