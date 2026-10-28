using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public class Refund : BaseEntity
{
    public const int ReasonMaxLength = 500;

    private Refund()
    {
    }

    public long BookingId { get; private set; }
    public long? TransactionId { get; private set; }
    public long? CancellationOperationId { get; private set; }
    public decimal Amount { get; private set; }
    public string? Reason { get; private set; }
    public RefundInitiator InitiatedBy { get; private set; }
    public RefundStatus Status { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public static Refund CreatePending(long bookingId, long transactionId, long cancellationOperationId,
        decimal amount, string? reason, RefundInitiator initiatedBy, DateTimeOffset requestedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bookingId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transactionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cancellationOperationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        if (reason?.Length > ReasonMaxLength)
            throw new ArgumentOutOfRangeException(nameof(reason));

        return new Refund
        {
            BookingId = bookingId,
            TransactionId = transactionId,
            CancellationOperationId = cancellationOperationId,
            Amount = amount,
            Reason = reason,
            InitiatedBy = initiatedBy,
            Status = RefundStatus.Pending,
            RequestedAtUtc = requestedAtUtc.ToUniversalTime(),
        };
    }
}