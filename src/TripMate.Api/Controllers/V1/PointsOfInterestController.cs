using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Administrator))]
[ForbiddenProblemDetails(
    PoiErrorCodes.AdminAccessRequired,
    PoiErrorMessages.AdminAccessRequired)]
[Route("api/v1/admin/pois")]
public sealed class PointsOfInterestController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost]
    [ProducesErrorResponseType(typeof(void))]
    [ProducesResponseType(typeof(PoiResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PossibleDuplicateProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Create(
        CreatePoiCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }
}