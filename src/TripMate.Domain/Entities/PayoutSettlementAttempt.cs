using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.PayoutSettlementAttempt (DAILY-PAYOUT-TRANSFER spec PD-7, PD-7a, PC-03). One row
/// per gateway call made for a <see cref="PayoutBatch"/> — the send itself, and every PD-7a
/// lookup made before a retry.
/// </summary>
public class PayoutSettlementAttempt : BaseEntity
{
    public const string OutcomeSent = "Sent";
    public const string OutcomeSucceeded = "Succeeded";
    public const string OutcomeFailed = "Failed";
    public const string OutcomeTimeout = "Timeout";
    public const string OutcomeRejected = "Rejected";

    public static readonly string[] AllOutcomes =
        [OutcomeSent, OutcomeSucceeded, OutcomeFailed, OutcomeTimeout, OutcomeRejected];

    private PayoutSettlementAttempt()
    {
    }

    public long BatchId { get; private set; }

    public int AttemptNumber { get; private set; }

    public DateTimeOffset SentAtUtc { get; private set; }

    public string GatewayReference { get; private set; } = string.Empty;

    public string Outcome { get; private set; } = OutcomeSent;

    public string? FailureReason { get; private set; }

    public static PayoutSettlementAttempt Create(
        long batchId, int attemptNumber, DateTimeOffset sentAtUtc, string gatewayReference,
        string outcome, string? failureReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayReference);
        if (!AllOutcomes.Contains(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));

        return new PayoutSettlementAttempt
        {
            BatchId = batchId,
            AttemptNumber = attemptNumber,
            SentAtUtc = sentAtUtc,
            GatewayReference = gatewayReference,
            Outcome = outcome,
            FailureReason = failureReason,
        };
    }
}