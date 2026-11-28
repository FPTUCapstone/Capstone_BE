using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.ChatGroups;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/chat-groups")]
public sealed class ChatGroupsController(ISender sender, ICurrentUserService user,
    IChatGroupNotifier notifier) : ControllerBase
{
    public sealed record CreateChatGroupRequest(string? Name, long[]? MemberUserIds, Guid ClientRequestId);
    public sealed record SendChatGroupRequest(string? Content, Guid ClientMessageId);
    public sealed record LeaveChatGroupRequest(long? ReplacementHostUserId);
    public sealed record ReadChatGroupRequest(long ThroughMessageId);
    public sealed record RenameChatGroupRequest(string? Name);

    private IActionResult HandleFailure(Result result) => Problem(title: result.ErrorMessage,
        statusCode: result.ErrorCode switch
        {
            ChatGroupErrors.Forbidden => StatusCodes.Status403Forbidden,
            ChatGroupErrors.NotFound => StatusCodes.Status404NotFound,
            ChatGroupErrors.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        });

    [HttpPost]
    public async Task<IActionResult> Start([FromBody] CreateChatGroupRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new CreateChatGroup(actor, request.Name, request.MemberUserIds, request.ClientRequestId), ct);
        if (result.IsFailure) return HandleFailure(result);
        await notifier.NotifyAsync(result.Value.GroupId, false, ct);
        return Ok(result.Value);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, [FromQuery] int limit = 50, [FromQuery] long? beforeGroupId = null)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new ListChatGroups(actor, limit, beforeGroupId), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/members")]
    public async Task<IActionResult> Members(long id, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new GetChatGroupMembers(actor, id), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/messages")]
    public async Task<IActionResult> Messages(long id, CancellationToken ct, [FromQuery] int limit = 50, [FromQuery] long? beforeMessageId = null)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new GetChatGroupMessages(actor, id, limit, beforeMessageId), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/messages")]
    public async Task<IActionResult> Send(long id, [FromBody] SendChatGroupRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new SendChatGroupMessage(actor, id, request.Content, request.ClientMessageId), ct);
        if (result.IsFailure) return HandleFailure(result);
        await notifier.NotifyAsync(id, true, ct);
        return Ok(result.Value);
    }

    [HttpPut("{id:long}/name")]
    public async Task<IActionResult> Rename(long id, [FromBody] RenameChatGroupRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new RenameChatGroup(actor, id, request.Name), ct);
        if (result.IsFailure) return HandleFailure(result);
        await notifier.NotifyAsync(id, false, CancellationToken.None);
        return Ok(result.Value);
    }

    [HttpDelete("{id:long}/members/{userId:long}")]
    public async Task<IActionResult> Kick(long id, long userId, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new KickChatGroupMember(actor, id, userId), ct);
        if (result.IsFailure) return HandleFailure(result);
        if (result.Value)
            await notifier.NotifyMembershipChangedAsync(id, userId, CancellationToken.None);
        return NoContent();
    }

    [HttpPost("{id:long}/dissolve")]
    public async Task<IActionResult> Dissolve(long id, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new DissolveChatGroup(actor, id), ct);
        if (result.IsFailure) return HandleFailure(result);
        await notifier.NotifyAsync(id, false, CancellationToken.None);
        return NoContent();
    }

    [HttpPost("{id:long}/leave")]
    public async Task<IActionResult> Leave(long id, [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        [FromBody] LeaveChatGroupRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new LeaveChatGroup(actor, id, idempotencyKey, request.ReplacementHostUserId), ct);
        if (result.IsFailure) return HandleFailure(result);
        // Handler returns only after commit, including durable replays after a lost response.
        await notifier.NotifyMembershipChangedAsync(id, actor, CancellationToken.None);
        return Ok(result.Value);
    }

    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> Read(long id, [FromBody] ReadChatGroupRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new ReadChatGroup(actor, id, request.ThroughMessageId), ct);
        if (result.IsFailure) return HandleFailure(result);
        await notifier.NotifyAsync(id, false, ct);
        return NoContent();
    }
}