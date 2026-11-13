using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-42 (SRS 3.8.4.3, spec v1.2): a Tour Operator's own actions on a refund awaiting
/// its confirmation or partner return.</summary>
[ApiController]
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/refunds")]
public sealed class OperatorRefundsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("{refundId:long}/confirm")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ConfirmBookingRefundResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(long refundId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ConfirmBookingRefundCommand(refundId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{refundId:long}/return")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ReportBookingRefundReturnResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReportReturn(long refundId, [FromBody] ReportBookingRefundReturnRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ReportBookingRefundReturnCommand(refundId, request?.ReturnReference),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}