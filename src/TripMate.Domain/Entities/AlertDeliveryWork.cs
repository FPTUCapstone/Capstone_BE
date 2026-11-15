namespace TripMate.Domain.Entities;

public sealed class AlertDeliveryWork
{
    private AlertDeliveryWork()
    {
    }

    public long NotificationId { get; private set; }
    public Notification Notification { get; private set; } = null!;
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public DateTimeOffset? ClaimExpiresAtUtc { get; private set; }

    public static AlertDeliveryWork Create(Notification notification, DateTimeOffset dueAtUtc)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return new AlertDeliveryWork
        {
            Notification = notification,
            NextAttemptAtUtc = dueAtUtc.ToUniversalTime(),
        };
    }

    public void Stop()
    {
        NextAttemptAtUtc = null;
        ClaimToken = null;
        ClaimExpiresAtUtc = null;
    }
}