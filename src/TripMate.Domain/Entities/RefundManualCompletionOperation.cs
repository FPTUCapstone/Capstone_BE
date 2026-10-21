using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Durable idempotency record for an administrator-confirmed manual refund settlement.</summary>
public sealed class RefundManualCompletionOperation : BaseEntity
{
    private RefundManualCompletionOperation()
    {
    }

    public long RefundId { get; private set; }
    public long ActorUserId { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string RequestHash { get; private set; } = string.Empty;
    public string GatewayReference { get; private set; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; private set; }

    public static RefundManualCompletionOperation Create(long refundId, long actorUserId, Guid idempotencyKey,
        string requestHash, string gatewayReference, DateTimeOffset completedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refundId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);
        ArgumentOutOfRangeException.ThrowIfEqual(idempotencyKey, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);

        return new RefundManualCompletionOperation
        {
            RefundId = refundId,
            ActorUserId = actorUserId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            GatewayReference = gatewayReference.Trim(),
            CompletedAtUtc = completedAtUtc.ToUniversalTime(),
        };
    }
}
