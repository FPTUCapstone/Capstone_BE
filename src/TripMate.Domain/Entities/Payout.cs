namespace TripMate.Domain.Entities;

/// <summary>Persistent payout request snapshot and lifecycle state.</summary>
public sealed class Payout
{
    public const int StatusMaxLength = 24;
    public const int RejectionReasonMinLength = 10;
    public const int RejectionReasonMaxLength = 500;
    public const int GatewayReferenceMaxLength = 200;
    public const int GatewayFailureReasonMaxLength = 500;
    public const int BeneficiaryBankNameMaxLength = 100;
    public const int BeneficiaryAccountHolderMaxLength = 200;
    public const int BeneficiaryAccountLast4Length = 4;

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

    /// <summary>
    /// Minimal PendingReview constructor, guarding the same invariants as
    /// CK_Payouts_PeriodOrder/CK_Payouts_NetAmount/CK_Payouts_LifecycleState. This is only the
    /// entity shape — settlement-period eligibility, duplicate-request checking and the
    /// gross/commission calculation from bookings belong to UC-46 (Request Payout Settlement),
    /// which is not implemented in this codebase yet. Exists so tests (and, later, UC-46) can
    /// construct a request without a public parameterless setter.
    /// </summary>
    public static Payout CreatePendingReview(
        long operatorUserId,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal grossRevenue,
        decimal commissionAmount,
        DateTimeOffset requestedAtUtc,
        string? beneficiaryBankName = null,
        string? beneficiaryAccountHolder = null,
        string? beneficiaryAccountLast4 = null)
    {
        if (operatorUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operatorUserId));
        }

        if (periodEnd < periodStart)
        {
            throw new ArgumentException("periodEnd must not be before periodStart.", nameof(periodEnd));
        }

        if (grossRevenue < 0 || commissionAmount < 0 || commissionAmount > grossRevenue)
        {
            throw new ArgumentException("commissionAmount must be between 0 and grossRevenue.");
        }

        // Mirrors CK_Payouts_BeneficiarySnapshot: all three present, or all three absent.
        var hasBeneficiary = beneficiaryBankName is not null || beneficiaryAccountHolder is not null
            || beneficiaryAccountLast4 is not null;
        if (hasBeneficiary && (beneficiaryBankName is null || beneficiaryAccountHolder is null
            || beneficiaryAccountLast4 is null || !beneficiaryAccountLast4.All(char.IsAsciiDigit)
            || beneficiaryAccountLast4.Length != BeneficiaryAccountLast4Length))
        {
            throw new ArgumentException(
                "Beneficiary bank name, account holder and a 4-digit last4 must all be set together, or all left null.");
        }

        return new Payout
        {
            OperatorUserId = operatorUserId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            GrossRevenue = grossRevenue,
            CommissionAmount = commissionAmount,
            NetAmount = grossRevenue - commissionAmount,
            Status = StatusPendingReview,
            RequestedAtUtc = requestedAtUtc,
            BeneficiaryBankName = beneficiaryBankName,
            BeneficiaryAccountHolder = beneficiaryAccountHolder,
            BeneficiaryAccountLast4 = beneficiaryAccountLast4,
        };
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

    /// <summary>Beneficiary bank snapshot captured at request time (UC-46). Null until UC-46 writes it.</summary>
    public string? BeneficiaryBankName { get; private set; }

    public string? BeneficiaryAccountHolder { get; private set; }

    /// <summary>Last 4 digits only. The full account number is never stored on this entity.</summary>
    public string? BeneficiaryAccountLast4 { get; private set; }

    /// <summary>
    /// UC-73: moves a request from PendingReview to ApprovedForSettlement. Returns false without
    /// changing state when the request is not currently PendingReview (caller maps that to a 409).
    /// </summary>
    public bool Approve(long reviewerUserId, DateTimeOffset reviewedAtUtc)
    {
        if (reviewerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reviewerUserId));
        }

        if (Status != StatusPendingReview)
        {
            return false;
        }

        Status = StatusApprovedForSettlement;
        ReviewedBy = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc;
        return true;
    }

    /// <summary>
    /// UC-74: moves a request from PendingReview to Rejected with a required reason. Returns false
    /// without changing state when the request is not currently PendingReview (caller maps that to
    /// a 409). The reason is expected to already be trimmed and within
    /// [RejectionReasonMinLength, RejectionReasonMaxLength]; this is a defensive invariant guard,
    /// not where the Staff-facing length rule is enforced (that belongs to the Application layer).
    /// </summary>
    public bool Reject(long reviewerUserId, string reason, DateTimeOffset reviewedAtUtc)
    {
        if (reviewerUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reviewerUserId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length < RejectionReasonMinLength || reason.Length > RejectionReasonMaxLength)
        {
            throw new ArgumentException(
                $"Rejection reason must be between {RejectionReasonMinLength} and {RejectionReasonMaxLength} characters.",
                nameof(reason));
        }

        if (Status != StatusPendingReview)
        {
            return false;
        }

        Status = StatusRejected;
        ReviewedBy = reviewerUserId;
        ReviewedAtUtc = reviewedAtUtc;
        RejectionReason = reason;
        return true;
    }
}