namespace TripMate.Application.Features.Bookings.Common;

public static class BookingCancellationErrorCodes
{
    public const string Unauthorized = "booking.cancellation.unauthorized";
    public const string NotFound = "booking.not_found";
    public const string Forbidden = "booking.cancellation.forbidden";
    public const string InvalidState = "booking.cancellation.invalid_state";
    public const string ReasonRequired = "booking.cancellation.reason_required";
    public const string PaymentNotRefundable = "booking.cancellation.payment_not_refundable";
    public const string WindowClosed = "booking.cancellation.window_closed";
    public const string PolicyUnavailable = "booking.cancellation.policy_unavailable";
    public const string IdempotencyPayloadMismatch = "booking.cancellation.idempotency_payload_mismatch";
    public const string ConcurrentRetryExhausted = "booking.cancellation.concurrent_retry_exhausted";
    public const string RefundManualReviewRequired = "booking.refund.manual_review_required";
    public const string RefundInvalidState = "booking.refund.invalid_state";
}