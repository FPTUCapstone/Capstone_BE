using MediatR;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.CheckIn.Common;

namespace TripMate.Application.Features.Operator.CheckIn.Scan;

/// <summary>
/// UC-43 command: Checks in a traveler for a departure schedule using either scanned QR content
/// or manually entered booking/ticket code.
/// </summary>
public sealed record CheckInTravelerCommand(
    long ScheduleId,
    string? QrContent = null,
    string? BookingCode = null,
    decimal? Latitude = null,
    decimal? Longitude = null) : IRequest<Result<CheckInResultDto>>;