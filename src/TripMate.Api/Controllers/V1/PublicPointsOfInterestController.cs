using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Features.PointsOfInterest.Detail;
using TripMate.Application.Features.PointsOfInterest.Explore;

namespace TripMate.Api.Controllers.V1;

[AllowAnonymous]
[Route("api/v1/pois")]
public sealed class PublicPointsOfInterestController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesErrorResponseType(typeof(void))]
    [ProducesResponseType(typeof(PagedPoiResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Explore(
        [FromQuery] ExplorePoisQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(query, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : HandleFailure(result);
    }

    [HttpGet("{id:long}")]
    [ProducesErrorResponseType(typeof(void))]
    [ProducesResponseType(typeof(PoiDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDetail(
        [FromRoute] long id,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetPoiDetailQuery(id), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : HandleFailure(result);
    }
}