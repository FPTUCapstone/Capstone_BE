using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using TripMate.Api.Common;
using TripMate.Application.Features.SupportChat;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize(Roles = nameof(UserRole.Traveler) + "," + nameof(UserRole.TourOperator))]
[Route("api/v1/support-chat/messages")]
[EnableRateLimiting(SupportChatRateLimiter.PolicyName)]
public sealed class SupportChatController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost]
    [RequestSizeLimit(128 * 1024)]
    [ProducesResponseType(typeof(SupportChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Send(SendSupportChatMessage request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(request, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}