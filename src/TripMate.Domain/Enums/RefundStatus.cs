namespace TripMate.Domain.Enums;

public enum RefundStatus
{
    Pending,
    Processing,
    RetryPending,
    Processed,
    Failed,
    Rejected,
}