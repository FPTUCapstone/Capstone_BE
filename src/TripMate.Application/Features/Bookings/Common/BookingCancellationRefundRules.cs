using TripMate.Application.Features.Bookings.Cancel;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Bookings.Common;

public readonly record struct BookingCancellationRefundDecision(decimal RefundAmount, string? ErrorCode, string? ErrorMessage);

public static class BookingCancellationRefundRules
{
    public static BookingCancellationRefundDecision Evaluate(Booking booking, bool isTraveler, DateTimeOffset nowUtc,
        bool confirmed, PaymentTransaction? successfulPayment)
    {
        if (booking.Status == BookingStatus.PendingPayment) return new(0m, null, null);
        if (!isTraveler) return new(booking.TotalAmount, null, null);
        if (!TravelerCancellationPolicy.TryParse(booking.CancellationPolicySnapshot, out var policy))
        {
            return new(0m, BookingCancellationErrorCodes.PolicyUnavailable,
                "The booking cancellation policy is unavailable.");
        }

        // BR-157 (the "paid within the last 24h always refunds in full" rule) was retired by the
        // owner decision of 11/11/2026 — only the BR-135 tier (days before departure) decides the
        // refund amount now, regardless of how recently the booking was paid.
        decimal? percent = policy!.RefundPercent(nowUtc, booking.TourSchedule!.StartAtUtc);
        if (percent is null)
        {
            return new(0m, BookingCancellationErrorCodes.WindowClosed,
                "Bookings cannot be cancelled less than 24 hours before departure.");
        }

        if (percent == 0m && !confirmed)
        {
            return new(0m, BookingCancellationErrorCodes.ConfirmationRequired,
                "Cancelling this close to departure refunds nothing. Confirm the cancellation to continue.");
        }

        return new(decimal.Round(booking.TotalAmount * percent.Value / 100m, 2, MidpointRounding.AwayFromZero), null, null);
    }
}