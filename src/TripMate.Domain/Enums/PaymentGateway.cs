namespace TripMate.Domain.Enums;

/// <summary>
/// Matches the payment.PaymentTransactions.gateway SQL check constraint.
/// </summary>
public enum PaymentGateway
{
    Mock = 1,
    PayOS = 2,
    VNPAY = 3,
    MoMo = 4,
    Stripe = 5,
}
