using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Payouts.DailySettlement.Reads;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// "Payout Settlement History" (SRS v11 PD-13) — a Tour Operator's own daily settlements.
/// Mounted at a distinct path from the legacy <c>/api/v1/operator/payouts</c> (the UC-46
/// payout-request model) so the two do not collide while both exist in the codebase.
/// </summary>
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/payout-settlements")]
public sealed class OperatorPayoutSettlementsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new GetOperatorPayoutSettlementHistoryQuery(page, pageSize), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
