using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Common;

public sealed record TourBookingPricingLine(ParticipantType ParticipantType, int Quantity, decimal UnitPrice)
{
    public decimal LineAmount => Quantity * UnitPrice;
}

public sealed record TourBookingPricingResult(
    IReadOnlyList<TourBookingPricingLine> Lines,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    string? AppliedVoucherCode);
