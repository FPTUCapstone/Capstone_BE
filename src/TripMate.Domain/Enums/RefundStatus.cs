namespace TripMate.Domain.Enums;

public enum RefundStatus
{
    Pending,
    Processing,
    RetryPending,
    ManualReview,
    Processed,
    Failed,
    Rejected,
}