namespace TripMate.Domain.Entities;

/// <summary>Persistent payout request snapshot and lifecycle state.</summary>
public sealed class Payout
{
    public const int StatusMaxLength = 24;
    public const int RejectionReasonMaxLength = 500;
    public const int GatewayReferenceMaxLength = 200;
    public const int GatewayFailureReasonMaxLength = 500;

    public const string StatusPendingReview = "PendingReview";
    public const string StatusApprovedForSettlement = "ApprovedForSettlement";
    public const string StatusSettlementProcessing = "SettlementProcessing";
    public const string StatusSettled = "Settled";
    public const string StatusSettlementFailed = "SettlementFailed";
    public const string StatusRejected = "Rejected";

    public const string PayoutCodePrefix = "PO-";

    public static readonly string[] AllStatuses =
        [
            StatusPendingReview,
            StatusApprovedForSettlement,
            StatusSettlementProcessing,
            StatusSettled,
            StatusSettlementFailed,
            StatusRejected,
        ];

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

    public string Status { get; private set; } = StatusPendingReview;

    public DateTimeOffset? RequestedAtUtc { get; private set; }

    public long? ReviewedBy { get; private set; }

    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    public string? RejectionReason { get; private set; }

    public long? SettlementAuthorizedBy { get; private set; }

    public DateTimeOffset? SettlementAuthorizedAtUtc { get; private set; }

    public string? GatewayReference { get; private set; }

    public string? GatewayFailureReason { get; private set; }

    public int GatewayAttemptCount { get; private set; }

    public DateTimeOffset? LastGatewayAttemptAtUtc { get; private set; }
}
