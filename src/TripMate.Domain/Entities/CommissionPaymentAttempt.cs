using System.Security.Cryptography;

using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Domain.Entities;

public sealed class CommissionPaymentAttempt : BaseEntity
{
    public const long OrderNamespace = 8_000_000_000_000_000;
    public const long NamespaceSize = 1_000_000_000_000;
    public const string Creating = "Creating", Pending = "Pending", Success = "Success", Cancelled = "Cancelled";
    private CommissionPaymentAttempt() { }
    public long StatementId { get; private set; }
    public long OrderCode { get; private set; }
    public PaymentGateway Gateway { get; private set; }
    public long AmountVnd { get; private set; }
    public string Status { get; private set; } = Creating;
    public string? PaymentLinkId { get; private set; }
    public string? CheckoutUrl { get; private set; }
    public string? GatewayReference { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static bool IsCommissionOrderCode(long value) => value >= OrderNamespace && value < OrderNamespace + NamespaceSize;
    public static CommissionPaymentAttempt Create(long statementId, PaymentGateway gateway, long amount, DateTimeOffset now, DateTimeOffset expires)
    {
        if (statementId <= 0 || amount <= 0 || expires <= now) throw new ArgumentOutOfRangeException(nameof(amount));
        if (gateway is not (PaymentGateway.Mock or PaymentGateway.PayOS)) throw new ArgumentOutOfRangeException(nameof(gateway));
        Span<byte> bytes = stackalloc byte[8]; RandomNumberGenerator.Fill(bytes);
        long order = OrderNamespace + (long)(BitConverter.ToUInt64(bytes) % (ulong)NamespaceSize);
        return new()
        {
            StatementId = statementId,
            OrderCode = order,
            Gateway = gateway,
            AmountVnd = amount,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(),
            ExpiresAtUtc = expires.ToUniversalTime()
        };
    }
    public void AttachLink(string linkId, string? checkoutUrl, DateTimeOffset now)
    {
        if (Status != Creating) throw new InvalidOperationException("Payment is not awaiting a link.");
        if (string.IsNullOrWhiteSpace(linkId) || linkId.Length > 100 || checkoutUrl?.Length > 2000) throw new ArgumentException("Invalid payment link.");
        PaymentLinkId = linkId; CheckoutUrl = checkoutUrl; Status = Pending; UpdatedAtUtc = now.ToUniversalTime();
    }
    public void Cancel(DateTimeOffset now)
    {
        if (Status == Success) throw new InvalidOperationException("A successful payment cannot be cancelled.");
        Status = Cancelled; UpdatedAtUtc = now.ToUniversalTime();
    }
    public void Complete(string reference, DateTimeOffset now)
    {
        if (Status != Pending) throw new InvalidOperationException("Payment is not pending.");
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 150) throw new ArgumentException("Invalid payment reference.");
        GatewayReference = reference; Status = Success; UpdatedAtUtc = now.ToUniversalTime();
    }
}