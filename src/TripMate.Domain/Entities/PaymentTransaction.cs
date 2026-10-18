using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

/// <summary>
/// Source of payment-attempt history for payment.PaymentTransactions.
/// UC-28: An authenticated Traveler pays an eligible Booking via Mock or PayOS.
/// </summary>
public class PaymentTransaction : BaseEntity
{
    public const string DefaultCurrency = "VND";
    public const string TypePayment = "Payment";

    private PaymentTransaction()
    {
    }

    public long BookingId { get; private set; }

    public Booking Booking { get; private set; } = null!;

    public PaymentGateway Gateway { get; private set; }

    public string? GatewayTransactionRef { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = DefaultCurrency;

    public string TransactionType { get; private set; } = TypePayment;

    public PaymentTransactionStatus Status { get; private set; }

    public long OrderCode { get; private set; }

    public string? PaymentLinkId { get; private set; }

    public string? CheckoutUrl { get; private set; }

    public string? QrCode { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    public string? RawResponse { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PaymentTransaction Create(
        long bookingId,
        PaymentGateway gateway,
        decimal amount,
        long orderCode,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc = null,
        string? checkoutUrl = null,
        string? paymentLinkId = null,
        string? qrCode = null,
        string currency = DefaultCurrency)
    {
        if (bookingId <= 0) throw new ArgumentOutOfRangeException(nameof(bookingId));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (orderCode <= 0) throw new ArgumentOutOfRangeException(nameof(orderCode));

        return new PaymentTransaction
        {
            BookingId = bookingId,
            Gateway = gateway,
            Amount = amount,
            Currency = string.IsNullOrWhiteSpace(currency) ? DefaultCurrency : currency.Trim().ToUpperInvariant(),
            TransactionType = TypePayment,
            Status = PaymentTransactionStatus.Pending,
            OrderCode = orderCode,
            PaymentLinkId = paymentLinkId?.Trim(),
            CheckoutUrl = checkoutUrl?.Trim(),
            QrCode = qrCode?.Trim(),
            ExpiresAtUtc = expiresAtUtc?.ToUniversalTime(),
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
        };
    }

    public void UpdateCheckoutDetails(string? paymentLinkId, string? checkoutUrl)
    {
        PaymentLinkId = paymentLinkId?.Trim();
        CheckoutUrl = checkoutUrl?.Trim();
    }

    public bool MarkSuccess(string? gatewayRef, string? rawResponse, DateTimeOffset nowUtc)
    {
        if (Status == PaymentTransactionStatus.Success)
            return false;

        Status = PaymentTransactionStatus.Success;
        if (!string.IsNullOrWhiteSpace(gatewayRef))
            GatewayTransactionRef = gatewayRef.Trim();
        if (!string.IsNullOrWhiteSpace(rawResponse))
            RawResponse = rawResponse;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }

    public bool MarkCancelled(string? rawResponse, DateTimeOffset nowUtc)
    {
        if (Status != PaymentTransactionStatus.Pending)
            return false;

        Status = PaymentTransactionStatus.Cancelled;
        if (!string.IsNullOrWhiteSpace(rawResponse))
            RawResponse = rawResponse;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }

    public bool MarkExpired(string? rawResponse, DateTimeOffset nowUtc)
    {
        if (Status != PaymentTransactionStatus.Pending)
            return false;

        Status = PaymentTransactionStatus.Expired;
        if (!string.IsNullOrWhiteSpace(rawResponse))
            RawResponse = rawResponse;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }

    public bool MarkFailed(string? rawResponse, DateTimeOffset nowUtc)
    {
        if (Status != PaymentTransactionStatus.Pending)
            return false;

        Status = PaymentTransactionStatus.Failed;
        if (!string.IsNullOrWhiteSpace(rawResponse))
            RawResponse = rawResponse;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
        return true;
    }
}