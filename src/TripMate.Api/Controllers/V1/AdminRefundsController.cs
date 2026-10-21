using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize(Roles = nameof(UserRole.Administrator))]
[Route("api/v1/admin/refunds")]
public sealed class AdminRefundsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("{refundId:long}/complete-manual")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ManualRefundCompletionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CompleteManual(long refundId, [FromBody] CompleteManualRefundRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out Guid parsedKey))
            return Problem(title: "A valid Idempotency-Key header is required.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await Sender.Send(new CompleteManualRefundCommand(refundId, parsedKey, request?.GatewayReference),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}