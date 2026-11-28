using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Api.Realtime;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.Chat;
using TripMate.Application.Features.TravelGroups.Chat.AcknowledgeBroadcast;
using TripMate.Application.Features.TravelGroups.Chat.CheckinRollCall;
using TripMate.Application.Features.TravelGroups.Chat.GetGroupMessages;
using TripMate.Application.Features.TravelGroups.Chat.MarkGroupChatRead;
using TripMate.Application.Features.TravelGroups.Chat.SendGroupMessage;
using TripMate.Application.Features.TravelGroups.Chat.ToggleExpensePayment;
using TripMate.Application.Features.TravelGroups.Chat.VotePoll;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = $"{nameof(UserRole.Traveler)},{nameof(UserRole.TourOperator)}")]
[Route("api/v1/travel-groups/{groupId:long}/messages")]
public class GroupMessagesController(
    ISender sender,
    ICurrentUserService currentUserService,
    IHubContext<GroupChatHub> hubContext,
    ILogger<GroupMessagesController> logger) : ApiControllerBase(sender)
{
    public sealed record EditRequest(string? Content, string? MetadataJson, long ExpectedRevision, Guid ClientRequestId);

    [HttpPut("{messageId:long}")]
    public async Task<IActionResult> Edit(long groupId, long messageId, [FromBody] EditRequest request, CancellationToken ct)
    {
        if (currentUserService.UserId is not { } actor) return Unauthorized();
        var result = await Sender.Send(new EditGroupMessage(groupId, messageId, actor, request.Content, request.MetadataJson, request.ExpectedRevision, request.ClientRequestId), ct);
        if (result.IsFailure) return HandleFailure(result);
        await NotifyAsync(groupId, "MessageUpdated", result.Value, ct);
        return Ok(result.Value);
    }
    [HttpGet("important")]
    public async Task<IActionResult> Important(long groupId, CancellationToken ct, [FromQuery] int limit = 20, [FromQuery] long? beforeMessageId = null)
    {
        if (currentUserService.UserId is not { } actor) return Unauthorized();
        var result = await Sender.Send(new GetImportantGroupMessages(groupId, actor, limit, beforeMessageId), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
    [HttpGet("{messageId:long}/votes")]
    public async Task<IActionResult> Votes(long groupId, long messageId, CancellationToken ct)
    {
        if (currentUserService.UserId is not { } actor) return Unauthorized();
        var result = await Sender.Send(new GetGroupPollVotes(groupId, messageId, actor), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<GroupMessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMessages(
        long groupId,
        [FromQuery] int limit = 50,
        [FromQuery] long? beforeMessageId = null,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (groupId <= 0)
        {
            return Problem(
                title: "A positive group ID is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = new GetGroupMessagesQuery(groupId, currentUserService.UserId.Value, limit, beforeMessageId);
        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(GroupMessageDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SendMessage(
        long groupId,
        [FromBody] SendGroupMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (groupId <= 0)
        {
            return Problem(
                title: "A positive group ID is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new SendGroupMessageCommand(
            groupId,
            currentUserService.UserId.Value,
            request.MessageType,
            request.Content,
            request.MetadataJson,
            request.ClientMessageId);

        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await NotifyAsync(groupId, "MessageReceived", result.Value, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpPost("read")]
    [ProducesResponseType(typeof(GroupChatReadCursorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> MarkRead(long groupId, [FromBody] MarkGroupChatReadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        var result = await Sender.Send(new MarkGroupChatReadCommand(groupId,
            currentUserService.UserId.Value, request.ThroughMessageId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{messageId:long}/acknowledge")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Acknowledge(
        long groupId,
        long messageId,
        CancellationToken cancellationToken = default,
        [FromQuery] long? expectedRevision = null)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (groupId <= 0 || messageId <= 0)
        {
            return Problem(
                title: "Positive group and message IDs are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new AcknowledgeBroadcastCommand(groupId, messageId, currentUserService.UserId.Value, expectedRevision);
        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await NotifyAsync(groupId, "BroadcastAcknowledged", new { messageId, userId = currentUserService.UserId.Value, readCount = result.Value }, cancellationToken);

        return Ok(result.Value);
    }

    [HttpPost("{messageId:long}/checkin")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Checkin(
        long groupId,
        long messageId,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (groupId <= 0 || messageId <= 0)
        {
            return Problem(
                title: "Positive group and message IDs are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new CheckinRollCallCommand(groupId, messageId, currentUserService.UserId.Value);
        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await NotifyAsync(groupId, "RollCallCheckedIn", new { messageId, userId = currentUserService.UserId.Value, presentCount = result.Value }, cancellationToken);

        return Ok(result.Value);
    }

    [HttpPost("{messageId:long}/toggle-payment")]
    [ProducesResponseType(typeof(IReadOnlyList<long>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> TogglePayment(
        long groupId,
        long messageId,
        [FromBody] TogglePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (groupId <= 0 || messageId <= 0)
        {
            return Problem(
                title: "Positive group and message IDs are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new ToggleExpensePaymentCommand(groupId, messageId, currentUserService.UserId.Value, request.IsPaid, request.ExpectedRevision);
        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await NotifyAsync(groupId, "ExpensePaymentUpdated", new { messageId, paidUserIds = result.Value }, cancellationToken);

        return Ok(result.Value);
    }

    [HttpPost("{messageId:long}/vote")]
    [ProducesResponseType(typeof(IReadOnlyDictionary<int, int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Vote(
        long groupId,
        long messageId,
        [FromBody] VotePollRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (groupId <= 0 || messageId <= 0)
        {
            return Problem(
                title: "Positive group and message IDs are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new VotePollCommand(groupId, messageId, currentUserService.UserId.Value, request.OptionIndex, request.ExpectedRevision);
        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await NotifyAsync(groupId, "PollVoteRecorded", new { messageId, userId = currentUserService.UserId.Value, optionIndex = request.OptionIndex, counts = result.Value }, cancellationToken);

        return Ok(result.Value);
    }
    private async Task NotifyAsync(long groupId, string eventName, object payload, CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await Sender.Send(new GetGroupChatRecipientsQuery(groupId), cancellationToken);
            await hubContext.Clients.Users(recipients).SendAsync(eventName, payload, cancellationToken);
        }
        catch (Exception)
        {
            // Persistence has committed. History/interaction reads provide the reload fallback.
            // Avoid logging message content, user identifiers or exception payloads.
            logger.LogWarning("Group chat notification {EventName} could not be delivered after persistence.", eventName);
        }
    }
}