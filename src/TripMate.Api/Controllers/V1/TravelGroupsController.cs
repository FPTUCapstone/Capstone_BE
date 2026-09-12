using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.CreateTravelGroup;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/**
 * [UC-17] Travel Groups Controller
 * Manages travel group creation and group lifecycle operations.
 *
 * Route: POST /api/v1/travel-groups
 * Input: CreateTravelGroupRequest (ItineraryId, GroupName)
 * Output: 201 Created with CreateTravelGroupResponse | 400 Bad Request | 401 Unauthorized | 403 Forbidden | 404 Not Found
 */
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/travel-groups")]
public class TravelGroupsController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpPost]
    [ProducesResponseType(typeof(CreateTravelGroupResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTravelGroupRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(Request.Headers["Idempotency-Key"], out var idempotencyKey)
            || idempotencyKey == Guid.Empty)
        {
            return Problem(
                title: "A valid Idempotency-Key header is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new CreateTravelGroupCommand(
            request.ItineraryId,
            request.GroupName,
            currentUserService.UserId.Value,
            idempotencyKey);

        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }
}
