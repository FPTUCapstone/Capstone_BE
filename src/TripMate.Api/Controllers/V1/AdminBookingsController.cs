using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Admin.Bookings.GetList;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Staff))]
[Route("api/v1/admin/bookings")]
public sealed class AdminBookingsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PlatformBookingsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(
        [FromQuery] GetPlatformBookingsQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}