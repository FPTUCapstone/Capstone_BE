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

        if (successfulPayment?.SucceededAtUtc is { } paidAtUtc && nowUtc - paidAtUtc <= TimeSpan.FromHours(24))
        {
            return new(booking.TotalAmount, null, null);
        }

        decimal? percent = policy!.RefundPercent(nowUtc, booking.TourSchedule!.StartAtUtc);
        if (percent is null)
        {
            return new(0m, BookingCancellationErrorCodes.WindowClosed,
                "Bookings cannot be cancelled less than 24 hours before departure.");
        }

        if (percent == 0m && !confirmed)
        {
            return new(0m, BookingCancellationErrorCodes.ConfirmationRequired,
                "Cancelling within 72 hours of departure refunds nothing. Confirm the cancellation to continue.");
        }

        return new(decimal.Round(booking.TotalAmount * percent.Value / 100m, 2, MidpointRounding.AwayFromZero), null, null);
    }
}