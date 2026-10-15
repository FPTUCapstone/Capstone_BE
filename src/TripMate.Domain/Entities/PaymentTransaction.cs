using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class PaymentTransaction : BaseEntity
{
    private PaymentTransaction()
    {
    }

    public long BookingId { get; private set; }
    public decimal Amount { get; private set; }
    public PaymentTransactionType TransactionType { get; private set; }
    public PaymentTransactionStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}