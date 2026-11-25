using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.DirectRefundTransfers. Immutable record of a transfer report submitted
/// by a TO. The 72-hour Traveler confirmation window starts at ReportedAtUtc.
/// The recipient snapshot is captured from DirectRefundCase at report time and never changes.
/// </summary>
public sealed class DirectRefundTransfer : BaseEntity
{
    public const int BankReferenceMaxLength = 200;
    public const int SnapshotFieldMaxLength = 200;
    public const int SnapshotAccountNumberMaxLength = 50;
    public const int SnapshotBankCodeMaxLength = 20;

    /// <summary>72 continuous hours for Traveler to confirm receipt.</summary>
    public static readonly TimeSpan TravelerConfirmationWindow = TimeSpan.FromHours(72);

    private DirectRefundTransfer()
    {
    }

    public Guid OperationId { get; private set; }

    public long CaseId { get; private set; }

    /// <summary>Amount the TO claims to have transferred, VND (integer).</summary>
    public long ReportedAmountVnd { get; private set; }

    public string BankReference { get; private set; } = string.Empty;

    /// <summary>Actual transfer timestamp as entered by the TO (not server-authoritative).</summary>
    public DateTimeOffset ActualTransferredAtUtc { get; private set; }

    /// <summary>Server-side timestamp when this transfer report was persisted.</summary>
    public DateTimeOffset ReportedAtUtc { get; private set; }

    /// <summary>
    /// ReportedAtUtc + 72 hours (continuous). No business-day calendar.
    /// Traveler must respond before this point; afterwards the system auto-completes.
    /// </summary>
    public DateTimeOffset ConfirmationDeadlineAtUtc { get; private set; }

    // Recipient snapshot — immutable at time of report
    public string SnapshotBankCode { get; private set; } = string.Empty;

    public string SnapshotAccountNumber { get; private set; } = string.Empty;

    public string SnapshotAccountHolder { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static DirectRefundTransfer Create(
        long caseId,
        long reportedAmountVnd,
        string bankReference,
        DateTimeOffset actualTransferredAtUtc,
        DateTimeOffset reportedAtUtc,
        string snapshotBankCode,
        string snapshotAccountNumber,
        string snapshotAccountHolder)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(caseId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reportedAmountVnd);
        ArgumentException.ThrowIfNullOrWhiteSpace(bankReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotBankCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotAccountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotAccountHolder);
        if (actualTransferredAtUtc > reportedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(actualTransferredAtUtc), "Transfer cannot be in the future relative to report time.");

        return new DirectRefundTransfer
        {
            OperationId = Guid.NewGuid(),
            CaseId = caseId,
            ReportedAmountVnd = reportedAmountVnd,
            BankReference = bankReference,
            ActualTransferredAtUtc = actualTransferredAtUtc.ToUniversalTime(),
            ReportedAtUtc = reportedAtUtc.ToUniversalTime(),
            ConfirmationDeadlineAtUtc = reportedAtUtc.ToUniversalTime().Add(TravelerConfirmationWindow),
            SnapshotBankCode = snapshotBankCode,
            SnapshotAccountNumber = snapshotAccountNumber,
            SnapshotAccountHolder = snapshotAccountHolder,
            CreatedAtUtc = reportedAtUtc.ToUniversalTime(),
        };
    }
}
