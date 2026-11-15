namespace TripMate.Domain.Enums;

// UC-42: cancellation creates either AwaitingPartnerReturn -> ReturnReported -> ReturnReceived ->
// Pending (ready for automatic processing) when an operator share is outstanding, or Pending
// immediately when its share is zero. PendingOperatorConfirmation is retained only for records
// created by the former workflow. Reconciliation and Escalated are human-review states.
public enum RefundStatus
{
    PendingOperatorConfirmation,
    AwaitingPartnerReturn,
    ReturnReported,
    ReturnReceived,
    Pending,
    Processing,
    RetryPending,
    ManualReview,
    Processed,
    Failed,
    Rejected,
    Reconciliation,
    Escalated,
}
