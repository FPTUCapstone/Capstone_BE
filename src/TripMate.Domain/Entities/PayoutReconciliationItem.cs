using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.PayoutReconciliationItem (DAILY-PAYOUT-TRANSFER spec PD-17, plan Task 7A).
/// Recorded whenever the retry sweep's PD-7a gateway lookup reveals a batch's real outcome
/// differs from what TripMate still believed (<see cref="PayoutSettlementStatus.Processing"/>) —
/// the gateway reports <see cref="PayoutSettlementStatus.Settled"/> or
/// <see cref="PayoutSettlementStatus.SettlementFailed"/> for a batch TripMate had not yet
/// resolved to either. A plain retry/timeout (the gateway confirming it is still unresolved) is
/// not a discrepancy and never creates one of these.
/// </summary>
public class PayoutReconciliationItem : BaseEntity
{
    public const string ObservedOutcomeSucceeded = "Succeeded";
    public const string ObservedOutcomeRejected = "Rejected";

    private PayoutReconciliationItem()
    {
    }

    public long BatchId { get; private set; }

    public DateTimeOffset DetectedAtUtc { get; private set; }

    /// <summary>What TripMate's own record still said before the lookup — always
    /// <see cref="PayoutSettlementStatus.Processing"/> today, since that is the only status the
    /// retry sweep ever looks a batch up from.</summary>
    public string ExpectedStatus { get; private set; } = string.Empty;

    /// <summary>What the gateway's lookup actually reported — <see cref="ObservedOutcomeSucceeded"/>
    /// or <see cref="ObservedOutcomeRejected"/>.</summary>
    public string ObservedOutcome { get; private set; } = string.Empty;

    public string? ObservedReference { get; private set; }

    public string? Note { get; private set; }

    public static PayoutReconciliationItem Create(
        long batchId, DateTimeOffset detectedAtUtc, string expectedStatus, string observedOutcome,
        string? observedReference, string? note) => new()
        {
            BatchId = batchId,
            DetectedAtUtc = detectedAtUtc,
            ExpectedStatus = expectedStatus,
            ObservedOutcome = observedOutcome,
            ObservedReference = observedReference,
            Note = note,
        };
}