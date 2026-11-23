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
    IHubContext<GroupChatHub> hubContext) : ApiControllerBase(sender)
{
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
    public async Task<IActionResult> SendMessage(
        long groupId,
        [FromBody] SendGroupMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();

        var command = new SendGroupMessageCommand(
            groupId,
            currentUserService.UserId.Value,
            request.MessageType,
            request.Content,
            request.MetadataJson);

        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        // Broadcast to SignalR room
        await hubContext.Clients
            .Group(GroupChatHub.GetGroupRoom(groupId))
            .SendAsync("MessageReceived", result.Value, cancellationToken);

        return StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpPost("{messageId:long}/acknowledge")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Acknowledge(
        long groupId,
        long messageId,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();

        var command = new AcknowledgeBroadcastCommand(messageId, currentUserService.UserId.Value);
        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await hubContext.Clients
            .Group(GroupChatHub.GetGroupRoom(groupId))
            .SendAsync("BroadcastAcknowledged", new { messageId, userId = currentUserService.UserId.Value, readCount = result.Value }, cancellationToken);

        return Ok(result.Value);
    }

    [HttpPost("{messageId:long}/checkin")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Checkin(
        long groupId,
        long messageId,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();

        var command = new CheckinRollCallCommand(messageId, currentUserService.UserId.Value);
        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await hubContext.Clients
            .Group(GroupChatHub.GetGroupRoom(groupId))
            .SendAsync("RollCallCheckedIn", new { messageId, userId = currentUserService.UserId.Value, presentCount = result.Value }, cancellationToken);

        return Ok(result.Value);
    }

    [HttpPost("{messageId:long}/toggle-payment")]
    [ProducesResponseType(typeof(IReadOnlyList<long>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TogglePayment(
        long groupId,
        long messageId,
        [FromBody] TogglePaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();

        var command = new ToggleExpensePaymentCommand(messageId, currentUserService.UserId.Value, request.IsPaid);
        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await hubContext.Clients
            .Group(GroupChatHub.GetGroupRoom(groupId))
            .SendAsync("ExpensePaymentUpdated", new { messageId, paidUserIds = result.Value }, cancellationToken);

        return Ok(result.Value);
    }

    [HttpPost("{messageId:long}/vote")]
    [ProducesResponseType(typeof(IReadOnlyDictionary<int, int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Vote(
        long groupId,
        long messageId,
        [FromBody] VotePollRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();

        var command = new VotePollCommand(messageId, currentUserService.UserId.Value, request.OptionIndex);
        var result = await Sender.Send(command, cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        await hubContext.Clients
            .Group(GroupChatHub.GetGroupRoom(groupId))
            .SendAsync("PollVoteRecorded", new { messageId, userId = currentUserService.UserId.Value, optionIndex = request.OptionIndex, counts = result.Value }, cancellationToken);

        return Ok(result.Value);
    }
}
