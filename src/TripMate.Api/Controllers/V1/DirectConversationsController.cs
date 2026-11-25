using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.DirectChat;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/direct-conversations")]
public sealed class DirectConversationsController(ISender sender, ICurrentUserService user,
    IDirectChatNotifier notifier) : ControllerBase
{
    public sealed record StartRequest(long OtherUserId);
    public sealed record SendRequest(string? Content, Guid ClientMessageId);
    public sealed record ReadRequest(long ThroughMessageId);

    private IActionResult HandleFailure(Result result) => Problem(title: result.ErrorMessage,
        statusCode: result.ErrorCode switch
        {
            DirectChatErrors.Forbidden => StatusCodes.Status403Forbidden,
            DirectChatErrors.NotFound => StatusCodes.Status404NotFound,
            DirectChatErrors.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        });

    [HttpGet("users")]
    public async Task<IActionResult> Users([FromQuery] string? search, CancellationToken ct, [FromQuery] int limit = 20)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new SearchDirectUsers(actor, search, limit), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    public async Task<IActionResult> Start([FromBody] StartRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new StartDirectConversation(actor, request.OtherUserId), ct);
        if (result.IsFailure) return HandleFailure(result);
        await notifier.NotifyAsync(result.Value.ConversationId, false, ct);
        return Ok(result.Value);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, [FromQuery] int limit = 50, [FromQuery] long? beforeConversationId = null)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new ListDirectConversations(actor, limit, beforeConversationId), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/messages")]
    public async Task<IActionResult> Messages(long id, CancellationToken ct, [FromQuery] int limit = 50, [FromQuery] long? beforeMessageId = null)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new GetDirectMessages(actor, id, limit, beforeMessageId), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/messages")]
    public async Task<IActionResult> Send(long id, [FromBody] SendRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new SendDirectMessage(actor, id, request.Content, request.ClientMessageId), ct);
        if (result.IsFailure) return HandleFailure(result);
        await notifier.NotifyAsync(id, true, ct);
        return Ok(result.Value);
    }

    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> Read(long id, [FromBody] ReadRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new ReadDirectConversation(actor, id, request.ThroughMessageId), ct);
        if (result.IsFailure) return HandleFailure(result);
        await notifier.NotifyAsync(id, false, ct);
        return NoContent();
    }
}