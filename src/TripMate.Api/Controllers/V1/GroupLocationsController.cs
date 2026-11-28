using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;


using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.LocationSharing;
using TripMate.Domain.Enums;
namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/travel-groups")]
public sealed class GroupLocationsController(ISender sender, ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    private Task<bool> IsActiveTravelerAsync(CancellationToken cancellationToken) =>
        Sender.Send(new CanUseGroupLocationsQuery(currentUserService.UserId ?? 0), cancellationToken);

    [HttpGet("location-sharing/active")]
    [ProducesResponseType(typeof(EnabledLocationSharingGroupsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetEnabledLocationSharingGroups(CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (!await IsActiveTravelerAsync(cancellationToken)) return Forbid();
        var result = await Sender.Send(
            new GetEnabledLocationSharingGroupsQuery(currentUserService.UserId.Value), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{groupId:long}/location-sharing")]
    [ProducesResponseType(typeof(LocationSharingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLocationSharing(long groupId, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (!await IsActiveTravelerAsync(cancellationToken)) return Forbid();
        var result = await Sender.Send(
            new GetLocationSharingQuery(groupId, currentUserService.UserId.Value), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("{groupId:long}/location-sharing")]
    [ProducesResponseType(typeof(LocationSharingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateLocationSharing(
        long groupId,
        [FromBody] UpdateGroupLocationSharingRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (!await IsActiveTravelerAsync(cancellationToken)) return Forbid();
        if (!request.Enabled.HasValue)
        {
            return Problem(title: "The enabled setting is required.", statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await Sender.Send(
            new UpdateLocationSharingCommand(groupId, currentUserService.UserId.Value, request.Enabled.Value),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("{groupId:long}/location")]
    [ProducesResponseType(typeof(GroupLocationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PublishLocation(
        long groupId,
        [FromBody] PublishGroupLocationRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (!await IsActiveTravelerAsync(cancellationToken)) return Forbid();
        if (!request.Latitude.HasValue || !request.Longitude.HasValue || string.IsNullOrWhiteSpace(request.SessionVersion))
        {
            return Problem(title: "Latitude, longitude and sessionVersion are required.", statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await Sender.Send(new PublishGroupLocationCommand(
            groupId, currentUserService.UserId.Value, request.Latitude.Value, request.Longitude.Value,
            request.SessionVersion),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{groupId:long}/locations")]
    [ProducesResponseType(typeof(GroupLocationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLocations(long groupId, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (!await IsActiveTravelerAsync(cancellationToken)) return Forbid();
        var result = await Sender.Send(
            new GetGroupLocationsQuery(groupId, currentUserService.UserId.Value), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpDelete("{groupId:long}/location")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearLocation(long groupId, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (!await IsActiveTravelerAsync(cancellationToken)) return Forbid();
        var result = await Sender.Send(
            new ClearGroupLocationCommand(groupId, currentUserService.UserId.Value), cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }

}