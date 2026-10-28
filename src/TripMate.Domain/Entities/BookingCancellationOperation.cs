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

    public static BookingCancellationOperation Create(long bookingId, long actorUserId,
        BookingCancellationActor actor, Guid idempotencyKey, string requestHash,
        decimal refundAmount, DateTimeOffset cancelledAtUtc) => new()
        {
            BookingId = bookingId,
            ActorUserId = actorUserId,
            Actor = actor,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            RefundAmount = refundAmount,
            CancelledAtUtc = cancelledAtUtc,
        };
}