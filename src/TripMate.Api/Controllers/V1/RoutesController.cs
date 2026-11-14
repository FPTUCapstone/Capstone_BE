using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Features.Routes;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Staff))]
[ForbiddenProblemDetails(RouteErrors.Msg126, RouteErrors.Forbidden)]
[Route("api/v1/admin/routes")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RoutesController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>Create an Active route and its immutable audit event atomically (UC-54).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RouteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create([FromBody] CreateRouteCommand command,
        [BindRequired, FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command with { IdempotencyKey = idempotencyKey }, cancellationToken);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : HandleFailure(result);
    }

    [HttpGet]
    [ProducesResponseType(typeof(RouteListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] ListRoutesQuery query, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("options")]
    [ProducesResponseType(typeof(RouteOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Options(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RouteOptionsQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}