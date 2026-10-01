namespace TripMate.Domain.Entities;

/// <summary>Read model for a settlement payout row in payment.Payouts (UC-63).</summary>
public sealed class Payout
{
    public const string StatusPending = "Pending";
    public const string StatusRequested = "Requested";
    public const string StatusConfirmed = "Confirmed";
    public const string StatusPaid = "Paid";
    public const string StatusRejected = "Rejected";

    public const string PayoutCodePrefix = "PO-";

    public static readonly string[] AllStatuses =
        [StatusPending, StatusRequested, StatusConfirmed, StatusPaid, StatusRejected];

    private Payout()
    {
    }

    public long Id { get; private set; }

    public long OperatorUserId { get; private set; }

    public OperatorProfile Operator { get; private set; } = null!;

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public decimal GrossRevenue { get; private set; }

    public decimal CommissionAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public string Status { get; private set; } = StatusPending;

    public DateTimeOffset? RequestedAtUtc { get; private set; }

    public long? ConfirmedBy { get; private set; }

    public DateTimeOffset? ConfirmedAtUtc { get; private set; }
}