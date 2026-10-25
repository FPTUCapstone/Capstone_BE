using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Notifications.GetInbox;
using TripMate.Application.Features.Notifications.MarkRead;

namespace TripMate.Api.Controllers.V1;

[Authorize]
[Route("api/v1/notifications")]
public sealed class NotificationsController(ISender sender, ICurrentUserService currentUserService)
    : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(GetNotificationInboxResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInbox(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int limit = 20,
        [FromQuery] long? beforeId = null,
        CancellationToken cancellationToken = default)
    {
        if (currentUserService.UserId is not long userId)
        {
            return Unauthorized();
        }

        if (limit is < 1 or > 50 || beforeId is <= 0)
        {
            return Problem(title: "Invalid notification pagination.", statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await Sender.Send(
            new GetNotificationInboxQuery(userId, unreadOnly, limit, beforeId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{notificationId:long}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkRead(long notificationId, CancellationToken cancellationToken)
    {
        if (currentUserService.UserId is not long userId)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new MarkNotificationReadCommand(notificationId, userId), cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }
}