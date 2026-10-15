using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Bookings.Cancel;

public sealed record CancelBookingCommand(long BookingId, Guid IdempotencyKey, string? ReasonCategory,
    string? ReasonDetail) : IRequest<Result<CancelBookingResponse>>;

public sealed record CancelBookingResponse(long BookingId, string Status, decimal RefundAmount,
    string RefundStatus, DateTimeOffset CancelledAtUtc);