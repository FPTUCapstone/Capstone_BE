using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Bookings.Preview;

public sealed record GetBookingCancellationPreviewQuery(long BookingId)
    : IRequest<Result<BookingCancellationPreviewResponse>>;

public sealed record BookingCancellationPreviewResponse(
    long BookingId,
    string TourTitle,
    DateTimeOffset DepartureAtUtc,
    int Quantity,
    decimal TotalAmount,
    string BookingStatus,
    string PaymentStatus,
    bool CanCancel,
    string? BlockingReasonCode,
    string? BlockingReason,
    decimal EstimatedRefundAmount,
    string RefundStatus);