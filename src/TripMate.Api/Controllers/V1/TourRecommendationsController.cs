using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.TourRecommendations.Common;
using TripMate.Application.Features.TourRecommendations.GetLatest;
using TripMate.Application.Features.TourRecommendations.Refresh;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Policy = TripReviewAuthorizationPolicies.ActiveTraveler)]
[Route("api/v1/tour-recommendations")]
public sealed class TourRecommendationsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(TourRecommendationPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetLatest(
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new GetLatestTourRecommendationsQuery(page), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("refresh")]
    [ProducesResponseType(typeof(TourRecommendationPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTourRecommendationsRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new RefreshTourRecommendationsCommand(
            request.Destination,
            request.StartDate,
            request.EndDate,
            request.MinBudget,
            request.MaxBudget), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
