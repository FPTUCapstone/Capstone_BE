using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Personalization.Recommendations;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/poi-recommendations")]
public sealed class PoiRecommendationsController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpPost]
    [ProducesResponseType(typeof(PoiRecommendationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRecommendations(
        [FromBody] GetPoiRecommendationsApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(new GetPoiRecommendationsQuery(
            currentUserService.UserId.Value,
            request.ExplorationLatitude,
            request.ExplorationLongitude,
            request.SearchRadiusKm,
            request.Limit), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : HandleFailure(result);
    }
}