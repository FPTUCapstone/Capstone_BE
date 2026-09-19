using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Scheduling.Create;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/scheduling-requests")]
public sealed class SchedulingRequestsController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpPost]
    [ProducesResponseType(typeof(SchedulingResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromBody] CreateSchedulingRequest request,
        [BindRequired, FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var command = new CreateSchedulingRequestCommand(
            currentUserService.UserId.Value,
            idempotencyKey,
            request.StartAt,
            request.TimeZoneId,
            request.StartLatitude,
            request.StartLongitude,
            request.ExplorationLatitude,
            request.ExplorationLongitude,
            request.EndPoiId,
            request.ReturnToStart,
            request.AvailableMinutes,
            request.TransportMode,
            request.SearchRadiusKm,
            request.BudgetVnd,
            request.MandatoryPoiIds,
            request.RestPreference);
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }
}
