using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.CreateTravelGroup;

namespace TripMate.Api.Controllers.V1;

/**
 * [UC-17] Travel Groups Controller
 * Manages travel group creation and group lifecycle operations.
 *
 * Route: POST /api/v1/travel-groups
 * Input: CreateTravelGroupRequest (ItineraryId, GroupName)
 * Output: 201 Created with CreateTravelGroupResponse | 400 Bad Request | 401 Unauthorized | 404 Not Found
 */
[Authorize]
[Route("api/v1/travel-groups")]
public class TravelGroupsController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpPost]
    [ProducesResponseType(typeof(CreateTravelGroupResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTravelGroupRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var command = new CreateTravelGroupCommand(
            request.ItineraryId,
            request.GroupName,
            currentUserService.UserId.Value);

        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }
}