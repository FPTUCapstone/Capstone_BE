using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Common;

/// <summary>
/// The single rule for "can this booking still be cancelled", shared by the cancellation command,
/// the cancellation preview, and the Tour Operator booking views. It covers only the booking state;
/// actor ownership, the Traveler time window, and payment evidence stay with their own callers.
/// </summary>
public static class BookingCancelEligibility
{
    public const string BlockedInvalidStatus = "InvalidStatus";
    public const string BlockedCheckedIn = "CheckedIn";

    public static (bool CanCancel, string? BlockedReason) Evaluate(BookingStatus status, bool hasUsedTicket)
    {
        if (status is not (BookingStatus.PendingPayment or BookingStatus.Confirmed))
            return (false, BlockedInvalidStatus);

        return hasUsedTicket ? (false, BlockedCheckedIn) : (true, null);
    }
}