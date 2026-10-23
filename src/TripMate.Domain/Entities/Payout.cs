namespace TripMate.Domain.Entities;

/// <summary>Persistent payout request snapshot and lifecycle state.</summary>
public sealed class Payout
{
    public const int StatusMaxLength = 24;
    public const int RejectionReasonMaxLength = 500;
    public const int GatewayReferenceMaxLength = 200;
    public const int GatewayFailureReasonMaxLength = 500;
    public const int PayoutCodeMaxLength = 24;
    public const string PayoutCodePrefix = "PO-";
    public const int BankNameMaxLength = 150;
    public const int AccountHolderNameMaxLength = 150;
    public const int AccountNumberMaskedMaxLength = 30;

    public const string StatusPendingReview = "PendingReview";
    public const string StatusApprovedForSettlement = "ApprovedForSettlement";
    public const string StatusSettlementProcessing = "SettlementProcessing";
    public const string StatusSettled = "Settled";
    public const string StatusSettlementFailed = "SettlementFailed";
    public const string StatusRejected = "Rejected";
    public const string StatusCancelled = "Cancelled";

    public static readonly string[] AllStatuses =
        [
            StatusPendingReview,
            StatusApprovedForSettlement,
            StatusSettlementProcessing,
            StatusSettled,
            StatusSettlementFailed,
            StatusRejected,
            StatusCancelled,
        ];

    /// <summary>Statuses that count as an active request for BR-113. Since one payout can now
    /// cover several settlement months (the "available balance" model, decided 2026-10-23), the
    /// uniqueness is per-operator, not per-month: a second concurrent request would double-claim
    /// the same outstanding balance.</summary>
    public static readonly string[] ActiveStatuses =
        [
            StatusPendingReview,
            StatusApprovedForSettlement,
            StatusSettlementProcessing,
            StatusSettlementFailed,
        ];

    private Payout()
    {
    }

    public long Id { get; private set; }

    public long OperatorUserId { get; private set; }

    public OperatorProfile Operator { get; private set; } = null!;

    public string PayoutCode { get; private set; } = string.Empty;

    /// <summary>Earliest contributing settlement month's first day.</summary>
    public DateOnly PeriodStart { get; private set; }

    /// <summary>Latest contributing settlement month's last day.</summary>
    public DateOnly PeriodEnd { get; private set; }

    /// <summary>How many distinct settlement months were folded into this payout.</summary>
    public int PeriodCount { get; private set; }

    /// <summary>The instant up to which bookings/refunds were included — nothing processed after
    /// this moment is in this snapshot, however it is later recomputed elsewhere (BR: immutable snapshot).</summary>
    public DateTimeOffset CutOffAtUtc { get; private set; }

    public decimal GrossRevenue { get; private set; }

    public decimal PaymentsReceived { get; private set; }

    public decimal DiscountsGiven { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public decimal NetSales { get; private set; }

    public decimal CommissionAmount { get; private set; }

    public decimal PayableNetBeforeAdjustments { get; private set; }

    public decimal AdjustmentsApplied { get; private set; }

    /// <summary>The amount actually requested for settlement: PayableNetBeforeAdjustments minus
    /// AdjustmentsApplied. Mirrors the legacy NetAmount column (kept for UC-63/64/65 compatibility).</summary>
    public decimal NetAmount { get; private set; }

    public int BookingCount { get; private set; }

    public int ParticipantCount { get; private set; }

    public decimal MinimumAmountChecked { get; private set; }

    public string? BankName { get; private set; }

    public string? AccountHolderName { get; private set; }

    public string? AccountNumberMasked { get; private set; }

    public string? AccountNumberProtected { get; private set; }

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

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    private readonly List<PayoutItem> _items = [];
    public IReadOnlyCollection<PayoutItem> Items => _items.AsReadOnly();

    private readonly List<PayoutRefundItem> _refundItems = [];
    public IReadOnlyCollection<PayoutRefundItem> RefundItems => _refundItems.AsReadOnly();

    private readonly List<PayoutPeriodBreakdown> _periodBreakdowns = [];
    public IReadOnlyCollection<PayoutPeriodBreakdown> PeriodBreakdowns => _periodBreakdowns.AsReadOnly();

    /// <summary>
    /// Creates a PendingReview payout snapshot covering one or more settlement months — the
    /// "available balance" model (decided 2026-10-23): a single request claims every closed,
    /// eligible, not-yet-paid month for the operator at once, with a per-month breakdown kept
    /// for audit. The caller (the request command handler) is responsible for recomputing the
    /// amounts via the shared calculator immediately before calling this, inside the same
    /// serializable transaction that checks BR-113/minimum.
    /// </summary>
    public static Payout Create(
        string payoutCode,
        long operatorUserId,
        IReadOnlyCollection<PayoutMonthTotal> months,
        DateTimeOffset cutOffAtUtc,
        decimal adjustmentsApplied,
        decimal minimumAmountChecked,
        string bankName,
        string accountHolderName,
        string accountNumberMasked,
        string accountNumberProtected,
        DateTimeOffset requestedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payoutCode);
        if (payoutCode.Length > PayoutCodeMaxLength)
            throw new ArgumentOutOfRangeException(nameof(payoutCode));
        ArgumentNullException.ThrowIfNull(months);
        if (months.Count == 0)
            throw new ArgumentException("A payout must cover at least one settlement month.", nameof(months));
        if (adjustmentsApplied < 0)
            throw new ArgumentOutOfRangeException(nameof(adjustmentsApplied));

        var paymentsReceived = months.Sum(m => m.PaymentsReceived);
        var discountsGiven = months.Sum(m => m.DiscountsGiven);
        var refundedAmount = months.Sum(m => m.RefundedAmount);
        var commissionAmount = months.Sum(m => m.CommissionAmount);
        var bookingCount = months.Sum(m => m.EligibleBookingCount);

        var netSales = paymentsReceived - refundedAmount;
        var payableBeforeAdjustments = netSales - commissionAmount;
        var payableNet = payableBeforeAdjustments - adjustmentsApplied;
        if (payableNet < 0)
            throw new ArgumentOutOfRangeException(nameof(adjustmentsApplied), "A payout cannot be created with a negative payable amount.");

        var orderedMonths = months.OrderBy(m => m.SettlementMonth).ToList();
        var periodStart = orderedMonths[0].SettlementMonth;
        var lastMonth = orderedMonths[^1].SettlementMonth;
        var periodEnd = lastMonth.AddMonths(1).AddDays(-1);

        var payout = new Payout
        {
            PayoutCode = payoutCode.Trim(),
            OperatorUserId = operatorUserId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            PeriodCount = months.Count,
            CutOffAtUtc = cutOffAtUtc,
            GrossRevenue = paymentsReceived + discountsGiven,
            PaymentsReceived = paymentsReceived,
            DiscountsGiven = discountsGiven,
            RefundedAmount = refundedAmount,
            NetSales = netSales,
            CommissionAmount = commissionAmount,
            PayableNetBeforeAdjustments = payableBeforeAdjustments,
            AdjustmentsApplied = adjustmentsApplied,
            NetAmount = payableNet,
            BookingCount = bookingCount,
            ParticipantCount = months.Sum(m => m.ParticipantCount),
            MinimumAmountChecked = minimumAmountChecked,
            BankName = bankName.Trim(),
            AccountHolderName = accountHolderName.Trim(),
            AccountNumberMasked = accountNumberMasked.Trim(),
            AccountNumberProtected = accountNumberProtected,
            Status = StatusPendingReview,
            RequestedAtUtc = requestedAtUtc,
        };

        foreach (var month in orderedMonths)
        {
            payout._periodBreakdowns.Add(PayoutPeriodBreakdown.Create(
                month.SettlementMonth, month.EligibleBookingCount, month.PaymentsReceived,
                month.DiscountsGiven, month.RefundedAmount, month.CommissionAmount, month.PayableNetAmount));
        }

        return payout;
    }

    /// <summary>Sets the final payout code once the identity Id is known (assigned right after
    /// the first SaveChanges that inserts this row — the code embeds that Id).</summary>
    public void AssignPayoutCode(string payoutCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payoutCode);
        if (payoutCode.Length > PayoutCodeMaxLength)
            throw new ArgumentOutOfRangeException(nameof(payoutCode));
        PayoutCode = payoutCode.Trim();
    }

    public void AddItem(PayoutItem item) => _items.Add(item ?? throw new ArgumentNullException(nameof(item)));

    public void AddRefundItem(PayoutRefundItem item) => _refundItems.Add(item ?? throw new ArgumentNullException(nameof(item)));

    /// <summary>Operator-initiated cancellation (UC-46). Allowed only from PendingReview.</summary>
    public bool Cancel(DateTimeOffset nowUtc)
    {
        if (Status != StatusPendingReview)
            return false;

        Status = StatusCancelled;
        CancelledAtUtc = nowUtc;
        return true;
    }

    public bool IsCancellable => Status == StatusPendingReview;

    /// <summary>
    /// UC-73: moves a request from PendingReview to ApprovedForSettlement. Returns false without
    /// changing state when the request is not currently PendingReview (caller maps that to a 409).
    /// </summary>
    public bool Approve(long reviewerUserId, DateTimeOffset reviewedAtUtc)
    {
        if (reviewerUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(reviewerUserId));

        if (Status != StatusPendingReview)
            return false;

        Status = StatusApprovedForSettlement;
        ReviewedBy = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc;
        return true;
    }

    /// <summary>
    /// UC-74: moves a request from PendingReview to Rejected with a required reason. Returns false
    /// without changing state when the request is not currently PendingReview (caller maps that to
    /// a 409). The reason is expected to already be trimmed and within RejectionReasonMaxLength;
    /// this is a defensive invariant guard, not where the Staff-facing length rule is enforced
    /// (that belongs to the Application layer). There is no minimum length — SRS UC-74 only
    /// requires the reason be present (decided 2026-10-22, scoped against the SRS text, which has
    /// no 10-character minimum and no MSG157).
    /// </summary>
    public bool Reject(long reviewerUserId, string reason, DateTimeOffset reviewedAtUtc)
    {
        if (reviewerUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(reviewerUserId));

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > RejectionReasonMaxLength)
            throw new ArgumentException(
                $"Rejection reason must be at most {RejectionReasonMaxLength} characters.", nameof(reason));

        if (Status != StatusPendingReview)
            return false;

        Status = StatusRejected;
        ReviewedBy = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc;
        RejectionReason = reason;
        return true;
    }
}

/// <summary>One settlement month's totals, as fed into <see cref="Payout.Create"/> to build both
/// the payout's grand total and its per-month <see cref="PayoutPeriodBreakdown"/> audit rows.</summary>
public sealed record PayoutMonthTotal(
    DateOnly SettlementMonth,
    int EligibleBookingCount,
    int ParticipantCount,
    decimal PaymentsReceived,
    decimal DiscountsGiven,
    decimal RefundedAmount,
    decimal CommissionAmount,
    decimal PayableNetAmount);