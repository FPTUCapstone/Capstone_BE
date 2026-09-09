using MediatR;

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
 * Input: CreateTravelGroupRequest (ItineraryId, GroupName, HostUserId)
 * Output: 201 Created with CreateTravelGroupResponse | 400 Bad Request | 404 Not Found
 */
[Route("api/v1/travel-groups")]
public class TravelGroupsController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpPost]
    [ProducesResponseType(typeof(CreateTravelGroupResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTravelGroupRequest request,
        CancellationToken cancellationToken)
    {
        var hostUserId = currentUserService.UserId ?? request.HostUserId ?? 1;

        var command = new CreateTravelGroupCommand(
            request.ItineraryId,
            request.GroupName,
            hostUserId);

        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }
}