using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

public sealed class ServiceProviderRequestOperation : BaseEntity
{
    public const string Decision = "Decision";
    public const string Outcome = "Outcome";

    private ServiceProviderRequestOperation() { }

    public long ServiceBookingId { get; private set; }
    public ServiceBooking ServiceBooking { get; private set; } = null!;
    public long ActorUserId { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string OperationType { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public string ResponseJson { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static ServiceProviderRequestOperation Create(ServiceBooking booking, long actorUserId,
        Guid key, string operationType, string requestHash, string responseJson, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);
        if (actorUserId <= 0 || key == Guid.Empty || requestHash.Length != 64
            || operationType is not (Decision or Outcome) || string.IsNullOrWhiteSpace(responseJson))
            throw new ArgumentException("The request operation is invalid.");
        return new ServiceProviderRequestOperation
        {
            ServiceBooking = booking,
            ActorUserId = actorUserId,
            IdempotencyKey = key,
            OperationType = operationType,
            RequestHash = requestHash,
            ResponseJson = responseJson,
            CreatedAtUtc = nowUtc.ToUniversalTime()
        };
    }
}