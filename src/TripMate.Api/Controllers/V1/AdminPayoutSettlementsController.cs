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

    [HttpPost("{batchId:long}/carry-over")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> CarryOver(long batchId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new CarryOverFailedPayoutBatchCommand(batchId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    public sealed record RecordManualSettlementRequest(string? TransferReference);

    [HttpPost("{batchId:long}/manual-settlement")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> RecordManualSettlement(
        long batchId, [FromBody] RecordManualSettlementRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RecordManualPayoutSettlementCommand(batchId, request.TransferReference), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
