using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Payouts.DailySettlement.Admin;
using TripMate.Application.Features.Payouts.DailySettlement.Reads;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// DAILY-PAYOUT-TRANSFER (UC-72, UC-73). Mounted at a distinct path from the legacy
/// <c>/api/v1/admin/payouts</c> (the UC-46 payout-request model, scheduled for its own removal
/// decision — see plan Task 0/1) so the two do not collide while both exist in the codebase.
/// </summary>
[Authorize(Roles = "Staff,Administrator")]
[Route("api/v1/admin/payout-settlements")]
public sealed class AdminPayoutSettlementsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] GetPayoutSettlementsQuery query, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{batchId:long}")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetDetail(long batchId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetFailedPayoutBatchDetailQuery(batchId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{batchId:long}/retry")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Retry(long batchId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RetryFailedPayoutBatchCommand(batchId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    public sealed record MarkResolvedRequest(string? Reason);

    [HttpPost("{batchId:long}/resolve")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> MarkResolved(
        long batchId, [FromBody] MarkResolvedRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new MarkPayoutBatchResolvedCommand(batchId, request.Reason), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>Part of UC-48 (BR-189, PD-9A, Q4): Staff-only unlock of a Tour Operator the daily
    /// run locked for unrecovered pending debt. Refused with MSG199 while debt remains.</summary>
    [HttpPost("tour-operators/{tourOperatorUserId:long}/unlock")]
    [Authorize(Roles = "Staff")]
    public async Task<IActionResult> UnlockTourOperator(long tourOperatorUserId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new UnlockTourOperatorCommand(tourOperatorUserId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}