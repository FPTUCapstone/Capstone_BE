using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

using TripMate.Api.Realtime;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.ChatGroups;
using TripMate.Application.Features.DirectChat;
using TripMate.Application.Features.ItineraryShares;
using TripMate.Application.Features.TravelGroups.Chat;
using TripMate.Domain.Entities;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/messaging/itinerary-shares")]
public sealed class ItinerarySharesController(ISender sender, ICurrentUserService user,
    IChatGroupNotifier chatGroups, IDirectChatNotifier direct,
    IHubContext<GroupChatHub> hub, ILogger<ItinerarySharesController> logger) : ControllerBase
{
    public sealed record ShareRequest(long ItineraryId, string? ConversationKind, long ConversationId, Guid ClientRequestId);

    private IActionResult HandleFailure(Result result) => Problem(title: result.ErrorMessage,
        statusCode: result.ErrorCode switch
        {
            ItineraryShareErrors.NotFound => StatusCodes.Status404NotFound,
            ItineraryShareErrors.Forbidden => StatusCodes.Status403Forbidden,
            ItineraryShareErrors.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        });

    [HttpPost]
    [ProducesResponseType(typeof(ItineraryShareReceipt), StatusCodes.Status200OK)]
    public async Task<IActionResult> Share([FromBody] ShareRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new ShareItinerary(actor, request.ItineraryId, request.ConversationKind,
            request.ConversationId, request.ClientRequestId), ct);
        if (result.IsFailure) return HandleFailure(result);
        if (result.Value.Created)
        {
            // The handler has committed both records before any transport side effect.
            try
            {
                switch (request.ConversationKind)
                {
                    case ItineraryShare.TravelGroup:
                        var recipients = await sender.Send(new GetGroupChatRecipientsQuery(request.ConversationId), ct);
                        await hub.Clients.Users(recipients).SendAsync("MessageReceived", result.Value.TravelMessage!, ct);
                        break;
                    case ItineraryShare.ChatGroup:
                        await chatGroups.NotifyAsync(request.ConversationId, true, ct);
                        break;
                    case ItineraryShare.Direct:
                        await direct.NotifyAsync(request.ConversationId, true, ct);
                        break;
                }
            }
            catch (Exception)
            {
                // HTTP history is the fallback. Do not turn a committed receipt into a failed send
                // or log snapshot data, identifiers, or transport exception payloads.
                logger.LogWarning("Itinerary share notification could not be delivered after persistence.");
            }
        }
        return Ok(result.Value.Receipt);
    }

    [HttpGet("{shareId:guid}")]
    [ProducesResponseType(typeof(ItineraryShareDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid shareId, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new GetItineraryShare(actor, shareId), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}