using TripMate.Domain.Common;

namespace TripMate.Domain.Entities;

/// <summary>
/// Maps payment.DirectRefundCases. Represents one refund obligation under the
/// OperatorDirect payment model. The TO transfers directly to the Traveler;
/// this entity tracks the full lifecycle from recipient confirmation to completion.
/// Amounts are in VND (integer).
/// </summary>
public sealed class DirectRefundCase : BaseEntity
{
    public const string SourcePolicyCancellation = "PolicyCancellation";
    public const string SourceToCancellation = "ToCancellation";
    public const string SourceEmergencyEvent = "EmergencyEvent";
    public const string SourcePostTripComplaint = "PostTripComplaint";

    public const string StatusAwaitingRecipient = "AwaitingRecipient";
    public const string StatusAwaitingTransfer = "AwaitingTransfer";
    public const string StatusAwaitingTravelerConfirmation = "AwaitingTravelerConfirmation";
    public const string StatusCompleted = "Completed";
    public const string StatusDisputed = "Disputed";

    public const string MethodTravelerConfirmed = "TravelerConfirmed";
    public const string MethodAutoCompletedAfter72Hours = "AutoCompletedAfter72Hours";
    public const string MethodAdminResolved = "AdminResolved";

    public const int RecipientBankCodeMaxLength = 20;
    public const int RecipientAccountNumberMaxLength = 50;
    public const int RecipientAccountHolderMaxLength = 200;
    public const int CompletionRuleVersionMaxLength = 20;

    /// <summary>72 continuous hours; no business-day calendar.</summary>
    public static readonly TimeSpan TransferWindow = TimeSpan.FromHours(72);

    private DirectRefundCase()
    {
    }

    public Guid OperationId { get; private set; }

    public long BookingId { get; private set; }

    public string SourceType { get; private set; } = string.Empty;

    public long? SourceRefId { get; private set; }

    /// <summary>Amount agreed to be refunded, VND (integer).</summary>
    public long RefundAmountVnd { get; private set; }

    /// <summary>Amount verified as paid by webhook/API server-to-server, VND.</summary>
    public long PaidAmountVnd { get; private set; }

    public string? RecipientBankCode { get; private set; }

    public string? RecipientAccountNumber { get; private set; }

    public string? RecipientAccountHolder { get; private set; }

    public DateTimeOffset? RecipientConfirmedAtUtc { get; private set; }

    public string Status { get; private set; } = StatusAwaitingRecipient;

    /// <summary>
    /// 72-continuous-hour deadline for the TO to submit transfer evidence.
    /// Set when both refund amount is locked and recipient info is confirmed.
    /// </summary>
    public DateTimeOffset? TransferDeadlineAtUtc { get; private set; }

    public bool IsOverdueFlag { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? CompletionMethod { get; private set; }

    public string? CompletionRuleVersion { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    // Dispute resolution — set when admin acts on a Disputed case
    public string? DisputeResolution { get; private set; }

    public string? DisputeResolutionReason { get; private set; }

    public DateTimeOffset? DisputeResolvedAtUtc { get; private set; }

    public static DirectRefundCase Create(
        long bookingId, string sourceType, long refundAmountVnd, long paidAmountVnd,
        DateTimeOffset nowUtc, long? sourceRefId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bookingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refundAmountVnd);
        ArgumentOutOfRangeException.ThrowIfNegative(paidAmountVnd);

        return new DirectRefundCase
        {
            OperationId = Guid.NewGuid(),
            BookingId = bookingId,
            SourceType = sourceType,
            SourceRefId = sourceRefId,
            RefundAmountVnd = refundAmountVnd,
            PaidAmountVnd = paidAmountVnd,
            Status = StatusAwaitingRecipient,
            CreatedAtUtc = nowUtc.ToUniversalTime(),
            UpdatedAtUtc = nowUtc.ToUniversalTime(),
        };
    }

    /// <summary>
    /// Traveler confirms their bank account for receiving the refund.
    /// Starts the 72-hour transfer deadline once recipient info is set.
    /// </summary>
    public void ConfirmRecipient(
        string bankCode, string accountNumber, string accountHolder, DateTimeOffset nowUtc)
    {
        if (Status != StatusAwaitingRecipient)
            throw new InvalidOperationException("Recipient can only be confirmed when status is AwaitingRecipient.");

        ArgumentException.ThrowIfNullOrWhiteSpace(bankCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountHolder);

        RecipientBankCode = bankCode;
        RecipientAccountNumber = accountNumber;
        RecipientAccountHolder = accountHolder;
        RecipientConfirmedAtUtc = nowUtc.ToUniversalTime();
        TransferDeadlineAtUtc = nowUtc.ToUniversalTime().Add(TransferWindow);
        Status = StatusAwaitingTransfer;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    /// <summary>Marks that TO has reported a transfer; moves to awaiting Traveler confirmation.</summary>
    public void MarkTransferReported(DateTimeOffset nowUtc)
    {
        if (Status != StatusAwaitingTransfer)
            throw new InvalidOperationException("Transfer can only be reported when status is AwaitingTransfer.");

        Status = StatusAwaitingTravelerConfirmation;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void Complete(string completionMethod, string? ruleVersion, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completionMethod);

        Status = StatusCompleted;
        CompletedAtUtc = nowUtc.ToUniversalTime();
        CompletionMethod = completionMethod;
        CompletionRuleVersion = ruleVersion;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void Dispute(DateTimeOffset nowUtc)
    {
        if (Status != StatusAwaitingTravelerConfirmation)
            throw new InvalidOperationException("Case can only be disputed from AwaitingTravelerConfirmation.");

        Status = StatusDisputed;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    public void FlagOverdue(DateTimeOffset nowUtc)
    {
        IsOverdueFlag = true;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }

    /// <summary>
    /// Admin resolves dispute by confirming transfer was valid — marks case Completed
    /// and records the decision for audit.
    /// </summary>
    public void ResolveDisputeCompleted(string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DisputeResolution = "Completed";
        DisputeResolutionReason = reason;
        DisputeResolvedAtUtc = nowUtc.ToUniversalTime();
        Complete(MethodAdminResolved, "1.0", nowUtc);
    }

    /// <summary>
    /// Admin requires TO to correct or provide more evidence — reopens case to
    /// AwaitingTransfer with a fresh 72-hour transfer window and records the decision.
    /// </summary>
    public void ResolveDisputeReopen(string resolution, string reason, DateTimeOffset nowUtc)
    {
        if (Status != StatusDisputed)
            throw new InvalidOperationException("Case must be Disputed to reopen.");

        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        DisputeResolution = resolution;
        DisputeResolutionReason = reason;
        DisputeResolvedAtUtc = nowUtc.ToUniversalTime();

        Status = StatusAwaitingTransfer;
        TransferDeadlineAtUtc = nowUtc.ToUniversalTime().Add(TransferWindow);
        IsOverdueFlag = false;
        UpdatedAtUtc = nowUtc.ToUniversalTime();
    }
}