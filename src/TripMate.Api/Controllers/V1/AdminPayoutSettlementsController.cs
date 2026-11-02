using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Admin.Payouts.GetList;
using TripMate.Application.Features.Admin.PayoutSettlements.Authorize;
using TripMate.Application.Features.Admin.PayoutSettlements.GetDetail;
using TripMate.Application.Features.Admin.PayoutSettlements.GetList;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-65 Administrator settlement scope of the payout module. Staff review stays on
/// <see cref="AdminPayoutsController"/>; neither role reaches the other's endpoints.</summary>
[Authorize(Roles = nameof(UserRole.Administrator))]
[Route("api/v1/admin/payout-settlements")]
public sealed class AdminPayoutSettlementsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(PayoutsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(
        [FromQuery] GetPayoutSettlementsQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{payoutId:long}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(PayoutSettlementDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDetail(long payoutId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetPayoutSettlementDetailQuery(payoutId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>Authorizes settlement of an ApprovedForSettlement payout. The transfer itself runs
    /// asynchronously afterwards, so success is 202 Accepted.</summary>
    [HttpPost("{payoutId:long}/authorize")]
    [ProducesResponseType(typeof(AuthorizePayoutSettlementResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Authorize(long payoutId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new AuthorizePayoutSettlementCommand(payoutId), cancellationToken);
        return result.IsSuccess ? Accepted(result.Value) : HandleFailure(result);
    }
}