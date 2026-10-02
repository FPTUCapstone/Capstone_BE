using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Payouts.GetList;

namespace TripMate.Application.Features.Admin.Payouts.GetDetails;

public sealed record GetPayoutDetailsQuery(long PayoutId)
    : IRequest<Result<PayoutDetailsDto>>;

public sealed record PayoutDetailsDto(
    string PayoutId,
    string PayoutCode,
    PayoutOperatorDto Operator,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal GrossRevenue,
    decimal CommissionRate,
    decimal CommissionAmount,
    decimal NetAmount,
    DateTimeOffset? RequestedAtUtc,
    string Status,
    IReadOnlyList<PayoutBookingDto> Bookings);

public sealed record PayoutBookingDto(
    string BookingId,
    string BookingCode,
    string? TourName,
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal NetAmount);