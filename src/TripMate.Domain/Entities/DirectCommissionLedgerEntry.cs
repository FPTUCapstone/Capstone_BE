using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>Immutable, exactly-once revenue debit or completed-refund credit on an issued statement.</summary>
public sealed class DirectCommissionLedgerEntry : BaseEntity
{
    public const string BookingRevenue = "BookingRevenue";
    public const string DirectRefundCredit = "DirectRefundCredit";
    public const string LegacyRefundCredit = "LegacyRefundCredit";
    public const int SourceTypeMaxLength = 24;
    private DirectCommissionLedgerEntry() { }
    public long StatementId { get; private set; }
    public DirectCommissionStatement Statement { get; private set; } = null!;
    public long BookingId { get; private set; }
    public string SourceType { get; private set; } = "";
    public long SourceId { get; private set; }
    public long PaymentTransactionId { get; private set; }
    public long BaseAmountVnd { get; private set; }
    public decimal CommissionRate { get; private set; }
    public long CommissionAmountVnd { get; private set; }
    public DateTimeOffset SourceOccurredAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static DirectCommissionLedgerEntry Create(DirectCommissionStatement statement, long bookingId, string sourceType,
        long sourceId, long paymentTransactionId, long baseAmount, decimal rate, long commissionAmount,
        DateTimeOffset sourceAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (bookingId <= 0 || sourceId <= 0 || paymentTransactionId <= 0 || rate is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(bookingId));
        if (sourceType == BookingRevenue ? baseAmount <= 0 || commissionAmount < 0
            : sourceType is not (DirectRefundCredit or LegacyRefundCredit) || baseAmount >= 0 || commissionAmount > 0)
            throw new ArgumentException("Invalid commission source or signed amount.");
        return new()
        {
            Statement = statement,
            BookingId = bookingId,
            SourceType = sourceType,
            SourceId = sourceId,
            PaymentTransactionId = paymentTransactionId,
            BaseAmountVnd = baseAmount,
            CommissionRate = rate,
            CommissionAmountVnd = commissionAmount,
            SourceOccurredAtUtc = sourceAt.ToUniversalTime(),
            CreatedAtUtc = now.ToUniversalTime()
        };
    }
}