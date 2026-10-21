namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the payment.PaymentTransactions.status SQL check constraint.
/// </summary>
public enum PaymentTransactionStatus
{
    Pending = 1,
    Success = 2,
    Cancelled = 3,
    Expired = 4,
    Failed = 5,
}