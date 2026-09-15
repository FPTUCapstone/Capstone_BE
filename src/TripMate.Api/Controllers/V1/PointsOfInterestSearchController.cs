using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.PointsOfInterest.Search;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/points-of-interest")]
public sealed class PointsOfInterestSearchController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("search")]
    [ProducesResponseType(typeof(SelectablePoiSearchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Search(
        [FromQuery] SearchSelectablePoisQuery request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(request, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : HandleFailure(result);
    }
}
