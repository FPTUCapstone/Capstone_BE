namespace TripMate.Application.Features.Bookings.Refunds;

/// <summary>
/// UC-42 SRS-alignment spec v1.2, §3.2: new error codes for the Tour Operator/Staff
/// confirm-and-return flow. Refund-not-found and role-unauthorized cases reuse the existing
/// <see cref="Common.BookingCancellationErrorCodes.NotFound"/>/<see cref="Common.BookingCancellationErrorCodes.Unauthorized"/>,
/// the same way <c>CompleteManualRefundCommand</c> already does.
/// </summary>
public static class RefundConfirmationErrorCodes
{
    /// <summary>MSG126: the signed-in Tour Operator does not own the booking's tour.</summary>
    public const string OperatorNotOwner = "refund.operator_not_owner";

    /// <summary>MSG171: the refund is not in the status this action requires.</summary>
    public const string RequestNotPending = "refund.request_not_pending";

    /// <summary>MSG172: a bank-transfer reference is required to report a return.</summary>
    public const string ReturnReferenceRequired = "refund.return_reference_required";

    /// <summary>MSG127: the status change could not be persisted; the refund keeps its prior status.</summary>
    public const string StatusPersistFailed = "refund.status_persist_failed";
}