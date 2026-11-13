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

    /// <summary>Settles (<see cref="RefundResolutionActions.Complete"/>, with a settlement reference
    /// and an Idempotency-Key) or closes without payment (<see cref="RefundResolutionActions.Reject"/>,
    /// with a reason) a refund that is Escalated or under Reconciliation.</summary>
    [HttpPost("{refundId:long}/resolve")]
    [Authorize(Roles = nameof(UserRole.Administrator))]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Resolve(long refundId, [FromBody] ResolveRefundRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.Equals(request?.Action, RefundResolutionActions.Reject, StringComparison.OrdinalIgnoreCase))
        {
            var rejected = await Sender.Send(new RejectReviewedRefundCommand(refundId, request!.Reason), cancellationToken);
            return rejected.IsSuccess ? Ok(rejected.Value) : HandleFailure(rejected);
        }

        if (!string.Equals(request?.Action, RefundResolutionActions.Complete, StringComparison.OrdinalIgnoreCase))
            return Problem(title: $"Action must be '{RefundResolutionActions.Complete}' or '{RefundResolutionActions.Reject}'.",
                statusCode: StatusCodes.Status400BadRequest);
        if (!Guid.TryParse(idempotencyKey, out Guid parsedKey))
            return Problem(title: "A valid Idempotency-Key header is required.",
                statusCode: StatusCodes.Status400BadRequest);

        var completed = await Sender.Send(
            new CompleteManualRefundCommand(refundId, parsedKey, request!.GatewayReference), cancellationToken);
        return completed.IsSuccess ? Ok(completed.Value) : HandleFailure(completed);
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

    [HttpGet]
    [Authorize(Roles = $"{nameof(UserRole.Staff)},{nameof(UserRole.Administrator)}")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(AdminRefundsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRefunds([FromQuery] string[]? status, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new GetAdminRefundsQuery(status, page, pageSize), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{refundId:long}")]
    [Authorize(Roles = $"{nameof(UserRole.Staff)},{nameof(UserRole.Administrator)}")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(AdminRefundDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRefundDetail(long refundId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetAdminRefundDetailQuery(refundId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}