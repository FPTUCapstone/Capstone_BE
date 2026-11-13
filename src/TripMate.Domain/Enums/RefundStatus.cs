namespace TripMate.Domain.Enums;

// UC-42 (SRS 3.8.4.3, spec v1.2): PendingOperatorConfirmation -> AwaitingPartnerReturn ->
// ReturnReported -> ReturnReceived -> Pending (ready for UC-67, BR-143) is the operator-share
// return path; a zero-share confirmation skips straight from PendingOperatorConfirmation to
// Pending (BR-139 case 4.b1). Reconciliation and Escalated are the two human-review terminal
// states (UC-41 6.b1 amount mismatch; BR-161 retries exhausted).
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