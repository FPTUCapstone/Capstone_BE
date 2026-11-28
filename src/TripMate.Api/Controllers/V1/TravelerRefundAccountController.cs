using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Traveler.RefundAccount;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/traveler/refund-account")]
public sealed class TravelerRefundAccountController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var result = await Sender.Send(new GetTravelerRefundAccountQuery(), cancellationToken);
        // Explicit JsonResult preserves JSON null rather than converting it to HTTP 204.
        return result.IsSuccess ? new JsonResult(result.Value) : HandleFailure(result);
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SaveTravelerRefundAccountCommand request, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var result = await Sender.Send(request, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}