using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class BookingCancellationOperation : BaseEntity
{
    private BookingCancellationOperation()
    {
    }

    public long BookingId { get; private set; }
    public long ActorUserId { get; private set; }
    public BookingCancellationActor Actor { get; private set; }
    public Guid IdempotencyKey { get; private set; }
    public string RequestHash { get; private set; } = string.Empty;
    public decimal RefundAmount { get; private set; }
    public DateTimeOffset CancelledAtUtc { get; private set; }

    /// <summary>UC-41 6.a1/6.b1 (SRS 3.8.4.2): the refund outcome actually recorded at the time of
    /// this cancellation — one of "NotRequired" (no verified payment, no refund created),
    /// <see cref="Domain.Enums.RefundStatus.PendingOperatorConfirmation"/> (normal UC-42 path), or
    /// <see cref="Domain.Enums.RefundStatus.Reconciliation"/> (amount mismatch, escalated).
    /// Frozen at creation like <see cref="RefundAmount"/>, so an idempotent replay (BR-138)
    /// reports the original outcome rather than whatever the linked refund has since become.</summary>
    public string RefundStatus { get; private set; } = "NotRequired";

    public static BookingCancellationOperation Create(long bookingId, long actorUserId,
        BookingCancellationActor actor, Guid idempotencyKey, string requestHash,
        decimal refundAmount, string refundStatus, DateTimeOffset cancelledAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refundStatus);
        return new()
        {
            BookingId = bookingId,
            ActorUserId = actorUserId,
            Actor = actor,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            RefundAmount = refundAmount,
            RefundStatus = refundStatus,
            CancelledAtUtc = cancelledAtUtc,
        };
    }
}