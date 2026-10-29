using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.Bookings.Get;
using TripMate.Application.Features.Operator.Bookings.List;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-40: customer bookings placed on the signed-in Tour Operator's own tours (read-only).</summary>
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/bookings")]
public sealed class OperatorBookingsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] long? tourId,
        [FromQuery] long? scheduleId,
        [FromQuery] BookingStatus? status,
        [FromQuery] PaymentStatus? paymentStatus,
        [FromQuery] DateOnly? departureFrom,
        [FromQuery] DateOnly? departureTo,
        [FromQuery] string? keyword,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = GetOperatorBookingsQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(
            new GetOperatorBookingsQuery(
                tourId, scheduleId, status, paymentStatus, departureFrom, departureTo, keyword, pageNumber, pageSize),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{bookingId:long}")]
    public async Task<IActionResult> Get(long bookingId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorBookingQuery(bookingId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}