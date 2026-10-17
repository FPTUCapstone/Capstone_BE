using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Bookings.Quote;

public sealed record QuoteParticipantCounts(int Adult, int Child, int Infant)
{
    public int Total => Adult + Child + Infant;
}

public sealed record QuoteTourBookingQuery(
    string TourScheduleId,
    QuoteParticipantCounts ParticipantCounts,
    string? VoucherCode) : IRequest<Result<QuoteTourBookingResponse>>;

public sealed record QuotePriceBreakdownLine(
    ParticipantType ParticipantType, int Quantity, decimal UnitPrice, decimal LineAmount);

public sealed record QuoteAppliedVoucher(string Code);

public sealed record QuoteTourBookingResponse(
    string TourId,
    string TourScheduleId,
    string Currency,
    QuoteParticipantCounts ParticipantCounts,
    IReadOnlyList<QuotePriceBreakdownLine> PriceBreakdown,
    decimal SubtotalAmount,
    decimal DiscountAmount,
    decimal TotalAmount,
    int RemainingSlots,
    QuoteAppliedVoucher? AppliedVoucher);