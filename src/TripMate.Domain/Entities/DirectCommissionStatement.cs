using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps commercial.TourDirectCommissionStatements. Monthly commission invoice for a TO
/// under the OperatorDirect payment model. Separate from ServiceCommissionStatement
/// (which covers non-tour commercial services).
/// The payable amount freezes ledger commission debits minus refund credits and prior-period credit.
/// </summary>
public sealed class DirectCommissionStatement : BaseEntity
{
    public const string StatusIssued = "Issued";
    public const string StatusPaymentReported = "PaymentReported";
    public const string StatusPaid = "Paid";
    public const string StatusOverdue = "Overdue";
    public const string StatusCancelled = "Cancelled";

    public const int StatementCodeMaxLength = 32;
    public const int PeriodMaxLength = 7;      // YYYY-MM
    public const int PayosPaymentLinkIdMaxLength = 100;

    /// <summary>7 calendar days (continuous) from issue_date.</summary>
    public static readonly int DueDays = 7;

    private DirectCommissionStatement()
    {
    }

    public string StatementCode { get; private set; } = string.Empty;

    public long TourOperatorUserId { get; private set; }

    /// <summary>Billing period in YYYY-MM format.</summary>
    public string Period { get; private set; } = string.Empty;

    /// <summary>
    /// Net source base recognized on this statement, floored at zero for display, VND (integer).
    /// The calculation/ledger retains gross sources and all credits, including excess carried forward.
    /// Rows with pending refunds/complaints are suspended until resolved.
    /// </summary>
    public long EligibleBaseVnd { get; private set; }

    /// <summary>Snapshotted at statement generation; stored as a fraction (e.g. 0.1000 = 10%).</summary>
    public decimal CommissionRate { get; private set; }

    /// <summary>Frozen amount due after source commission and carried credits, VND.</summary>
    public long CommissionAmountVnd { get; private set; }

    public string Status { get; private set; } = StatusIssued;

    public string? PayosPaymentLinkId { get; private set; }

    public long PaidAmountVnd { get; private set; }

    public DateTimeOffset? PaidAtUtc { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static DirectCommissionStatement Issue(
        string statementCode,
        long tourOperatorUserId,
        string period,
        long eligibleBaseVnd,
        decimal commissionRate,
        long commissionAmountVnd,
        DateOnly issueDate,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statementCode);
        if (statementCode.Length > StatementCodeMaxLength)
            throw new ArgumentOutOfRangeException(nameof(statementCode));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tourOperatorUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(period);
        ArgumentOutOfRangeException.ThrowIfNegative(eligibleBaseVnd);
        if (commissionRate is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(commissionRate));
        ArgumentOutOfRangeException.ThrowIfNegative(commissionAmountVnd);

        return new DirectCommissionStatement
        {
            StatementCode = statementCode,
            TourOperatorUserId = tourOperatorUserId,
            Period = period,
            EligibleBaseVnd = eligibleBaseVnd,
            CommissionRate = commissionRate,
            CommissionAmountVnd = commissionAmountVnd,
            Status = StatusIssued,
            PaidAmountVnd = 0L,
            IssueDate = issueDate,
            DueDate = issueDate.AddDays(DueDays),
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    public void RecordPaymentLink(string payosLinkId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payosLinkId);
        PayosPaymentLinkId = payosLinkId;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void MarkPaid(long paidAmountVnd, DateTimeOffset paidAtUtc, DateTimeOffset nowUtc)
    {
        if (paidAmountVnd <= 0 || paidAmountVnd != CommissionAmountVnd) throw new ArgumentOutOfRangeException(nameof(paidAmountVnd));
        if (Status == StatusPaid) return;
        if (Status is not (StatusIssued or StatusOverdue)) throw new InvalidOperationException("Statement is not payable.");
        Status = StatusPaid;
        PaidAmountVnd = paidAmountVnd;
        PaidAtUtc = paidAtUtc.ToUniversalTime();
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void MarkOverdue(DateTimeOffset nowUtc)
    {
        if (Status == StatusPaid || Status == StatusCancelled)
            return;
        Status = StatusOverdue;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }
}