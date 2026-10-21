using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Traveler.Payments.Webhook;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// Anonymous webhook receiver for PayOS payment notifications.
/// </summary>
[AllowAnonymous]
[Route("api/v1/payments/payos")]
public sealed class PayOsWebhookController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReceiveWebhook(
        [FromBody] PayOsWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ProcessPayOsWebhookCommand(payload), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : HandleFailure(result);
    }
}