using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Traveler.Bookings.Create;
using TripMate.Application.Features.Traveler.Bookings.Emergency;
using TripMate.Application.Features.Traveler.Bookings.Quote;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-27 Book Tour.</summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/traveler/tour-bookings")]
public sealed class TravelerTourBookingsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("emergency-notices")]
    [ProducesResponseType(typeof(IReadOnlyList<TourBookingEmergencyNoticeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListEmergencyNotices(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ListTourBookingEmergencyNoticesQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{bookingId:long}/emergency-status")]
    [ProducesResponseType(typeof(TourBookingEmergencyStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEmergencyStatus(
        long bookingId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new GetTourBookingEmergencyStatusQuery(bookingId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{bookingId:long}/emergency-status/acknowledge")]
    [ProducesResponseType(typeof(TourBookingEmergencyAcknowledgeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeEmergencyStatus(
        long bookingId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new AcknowledgeTourBookingEmergencyStatusCommand(bookingId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("quote")]
    [ProducesResponseType(typeof(QuoteTourBookingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Quote(QuoteTourBookingQuery query, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateTourBookingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateTourBookingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        CreateTourBookingCommand command,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command with { IdempotencyKey = idempotencyKey }, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return result.Value.IsReplay
            ? Ok(result.Value)
            : StatusCode(StatusCodes.Status201Created, result.Value);
    }
}
