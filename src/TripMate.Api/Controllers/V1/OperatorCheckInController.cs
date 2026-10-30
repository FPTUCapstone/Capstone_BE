using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripMate.Api.Common;
using TripMate.Application.Features.Operator.CheckIn.Common;
using TripMate.Application.Features.Operator.CheckIn.Departures;
using TripMate.Application.Features.Operator.CheckIn.Progress;
using TripMate.Application.Features.Operator.CheckIn.Scan;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// UC-43 Check In Traveler by QR Code (Mobile Screen #88).
/// Provides QR/manual check-in, real-time departure boarding progress, and departure schedule overview.
/// </summary>
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/check-in")]
public sealed class OperatorCheckInController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// Scans a traveler's e-ticket QR code or processes manual booking/ticket code entry (UC-43).
    /// </summary>
    [HttpPost("scan")]
    [ProducesResponseType(typeof(CheckInResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Scan(
        [FromBody] CheckInTravelerCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>
    /// Returns dynamic boarding progress (checked-in vs total confirmed participants) for a departure (D1).
    /// </summary>
    [HttpGet("schedules/{scheduleId:long}/progress")]
    [ProducesResponseType(typeof(DepartureProgressDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProgress(
        long scheduleId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetDepartureCheckInProgressQuery(scheduleId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>
    /// Returns the signed-in tour operator's departures for today (or a specified target date).
    /// </summary>
    [HttpGet("departures")]
    [ProducesResponseType(typeof(IReadOnlyList<OperatorDepartureSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDepartures(
        [FromQuery] DateOnly? targetDate,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorTodayDeparturesQuery(targetDate), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}