using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Bookings.Cancel;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize(Roles = nameof(UserRole.Traveler) + "," + nameof(UserRole.TourOperator))]
[Route("api/v1/bookings")]
public sealed class BookingsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("{bookingId:long}/cancellation")]
    [ProducesResponseType(typeof(CancelBookingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Cancel(
        long bookingId,
        [FromBody] CancelBookingRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out var parsedKey))
        {
            return Problem(title: "A valid Idempotency-Key header is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await Sender.Send(new CancelBookingCommand(bookingId, parsedKey,
            request?.ReasonCategory, request?.ReasonDetail), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}