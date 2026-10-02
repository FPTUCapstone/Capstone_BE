namespace TripMate.Domain.Entities;

/// <summary>Read model for a gateway payment transaction in payment.PaymentTransactions (UC-64).</summary>
public sealed class PaymentTransaction
{
    public const string TypePayment = "Payment";
    public const string TypeRefund = "Refund";

    public const string StatusPending = "Pending";
    public const string StatusSuccess = "Success";
    public const string StatusFailed = "Failed";

    private PaymentTransaction()
    {
    }

    public long Id { get; private set; }

    public long BookingId { get; private set; }

    public Booking Booking { get; private set; } = null!;

    public string Gateway { get; private set; } = string.Empty;

    public string? GatewayTransactionRef { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "VND";

    public string TransactionType { get; private set; } = TypePayment;

    public string Status { get; private set; } = StatusPending;

    public DateTimeOffset CreatedAtUtc { get; private set; }
}