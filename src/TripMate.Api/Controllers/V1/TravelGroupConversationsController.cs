using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.GetConversations;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler) + "," + nameof(UserRole.TourOperator))]
[Route("api/v1/travel-groups/conversations")]
public sealed class TravelGroupConversationsController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(GetTravelGroupConversationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(
        CancellationToken cancellationToken,
        [FromQuery] int limit = GetTravelGroupConversationsQuery.DefaultLimit,
        [FromQuery] long? beforeGroupId = null)
    {
        if (currentUserService.UserId is not > 0)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(new GetTravelGroupConversationsQuery(
            currentUserService.UserId.Value, limit, beforeGroupId), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}