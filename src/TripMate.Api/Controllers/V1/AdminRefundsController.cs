using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Bookings.Refunds;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("api/v1/admin/refunds")]
public sealed class AdminRefundsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("{refundId:long}/complete-manual")]
    [ProducesResponseType(typeof(ManualRefundCompletionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CompleteManual(long refundId, [FromBody] CompleteManualRefundRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new CompleteManualRefundCommand(refundId, request?.GatewayReference),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}