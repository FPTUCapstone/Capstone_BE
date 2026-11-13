using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// Administration-workspace actions on a refund. Each action states its own required role
/// (<see cref="UserRole.Administrator"/> or <see cref="UserRole.Staff"/>, BR-168) rather than a
/// single class-level one, since UC-42 step 6 (SRS 3.8.4.3) is a Staff action while manual
/// Administrator settlement is not.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/refunds")]
public sealed class AdminRefundsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("{refundId:long}/complete-manual")]
    [Authorize(Roles = nameof(UserRole.Administrator))]
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

    [HttpPost("{refundId:long}/return-received")]
    [Authorize(Roles = nameof(UserRole.Staff))]
    [Produces("application/json")]
    [ProducesResponseType(typeof(RecordBookingRefundReturnReceivedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReturnReceived(long refundId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RecordBookingRefundReturnReceivedCommand(refundId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}