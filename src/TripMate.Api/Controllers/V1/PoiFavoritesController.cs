using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.RecommendationFeedback.Favorites;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// The heart on a place detail page (UC-10 traveler feedback, D12). Signed-in travelers only; turning it on or
/// off is idempotent, and the ranking treats a favorite as a like.
/// </summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/pois/{poiId:long}/favorite")]
public sealed class PoiFavoritesController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(PoiFavoriteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(long poiId, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new GetPoiFavoriteQuery(currentUserService.UserId.Value, poiId),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut]
    [ProducesResponseType(typeof(PoiFavoriteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> Add(long poiId, CancellationToken cancellationToken) =>
        SetAsync(poiId, isFavorite: true, cancellationToken);

    [HttpDelete]
    [ProducesResponseType(typeof(PoiFavoriteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Remove(long poiId, CancellationToken cancellationToken) =>
        SetAsync(poiId, isFavorite: false, cancellationToken);

    private async Task<IActionResult> SetAsync(long poiId, bool isFavorite, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new SetPoiFavoriteCommand(currentUserService.UserId.Value, poiId, isFavorite),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}