using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.CreateTravelGroup;
using TripMate.Application.Features.TravelGroups.GetInvitation;
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

        var command = new CreateTravelGroupCommand(
            request.ItineraryId,
            request.GroupName,
            currentUserService.UserId.Value);

        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }

    /**
     * [UC-18] Get Group Invitation
     * Retrieves an active invitation code and QR deep link for the specified travel group.
     * Generates a new invitation if none exists or if expired.
     * Caller must be the Group Host.
     */
    [HttpGet("{groupId:long}/invitation")]
    [ProducesResponseType(typeof(GetGroupInvitationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvitation(
        [FromRoute] long groupId,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var query = new GetGroupInvitationQuery(groupId, currentUserService.UserId.Value);

        var result = await Sender.Send(query, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : HandleFailure(result);
    }
}
