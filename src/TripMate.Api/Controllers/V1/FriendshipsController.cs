using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Friendships;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/friendships")]
public sealed class FriendshipsController(ISender sender, ICurrentUserService user) : ControllerBase
{
    public sealed record UpdateRequest(string? Action);

    private IActionResult HandleFailure(Result result) => Problem(title: result.ErrorMessage,
        statusCode: result.ErrorCode switch
        {
            FriendshipErrors.Forbidden => StatusCodes.Status403Forbidden,
            FriendshipErrors.NotFound => StatusCodes.Status404NotFound,
            FriendshipErrors.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        });

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new ListFriendships(actor), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("{otherUserId:long}")]
    public async Task<IActionResult> Update(long otherUserId, [FromBody] UpdateRequest request, CancellationToken ct)
    {
        if (user.UserId is not { } actor) return Unauthorized();
        var result = await sender.Send(new UpdateFriendship(actor, otherUserId, request.Action), ct);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }
}