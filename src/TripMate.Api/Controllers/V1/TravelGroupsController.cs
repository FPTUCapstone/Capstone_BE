using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TravelGroups.CreateTravelGroup;
using TripMate.Application.Features.TravelGroups.GetInvitation;
using TripMate.Application.Features.TravelGroups.JoinTravelGroup;
using TripMate.Application.Features.TravelGroups.LocationSharing;
using TripMate.Application.Features.TravelGroups.ManageInvitation;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/**
 * [UC-17] Travel Groups Controller
 * Manages travel group creation and group lifecycle operations.
 *
 * Route: POST /api/v1/travel-groups
 * Input: CreateTravelGroupRequest (ItineraryId, GroupName)
 * Output: 201 Created with CreateTravelGroupResponse | 400 Bad Request | 401 Unauthorized | 403 Forbidden | 404 Not Found
 */
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/travel-groups")]
public class TravelGroupsController(
    ISender sender,
    ICurrentUserService currentUserService,
    IApplicationDbContext dbContext) : ApiControllerBase(sender)
{
    private Task<bool> IsActiveTravelerAsync(CancellationToken cancellationToken) =>
        dbContext.Users.AsNoTracking().AnyAsync(user => user.Id == currentUserService.UserId
            && user.Role == UserRole.Traveler
            && user.Status == AccountStatus.Active, cancellationToken);

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

    [HttpPost]
    [ProducesResponseType(typeof(CreateTravelGroupResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTravelGroupRequest request,
        [BindRequired, FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(idempotencyKey, out var parsedIdempotencyKey)
            || parsedIdempotencyKey == Guid.Empty)
        {
            return Problem(
                title: "A valid Idempotency-Key header is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new CreateTravelGroupCommand(
            request.ItineraryId,
            request.GroupName,
            currentUserService.UserId.Value,
            parsedIdempotencyKey);

        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : HandleFailure(result);
    }

    [HttpPost("join")]
    [ProducesResponseType(typeof(JoinTravelGroupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Join(
        [FromBody] JoinTravelGroupRequest request,
        [BindRequired, FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(idempotencyKey, out var parsedIdempotencyKey)
            || parsedIdempotencyKey == Guid.Empty)
        {
            return Problem(
                title: "A valid Idempotency-Key header is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new JoinTravelGroupCommand(
            request.InvitationCode,
            currentUserService.UserId.Value,
            parsedIdempotencyKey);

        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : HandleFailure(result);
    }

    [HttpPost("{groupId:long}/invitation")]
    [ProducesResponseType(typeof(GetGroupInvitationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> GetOrCreateInvitation(
        long groupId,
        [BindRequired, FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendInvitationCommandAsync(
            groupId,
            idempotencyKey,
            (currentUserId, key) => new GetOrCreateGroupInvitationCommand(groupId, currentUserId, key),
            cancellationToken);

    [HttpPost("{groupId:long}/invitation/regenerate")]
    [ProducesResponseType(typeof(GetGroupInvitationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> RegenerateInvitation(
        long groupId,
        [BindRequired, FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendInvitationCommandAsync(
            groupId,
            idempotencyKey,
            (currentUserId, key) => new RegenerateGroupInvitationCommand(groupId, currentUserId, key),
            cancellationToken);

    private async Task<IActionResult> SendInvitationCommandAsync(
        long groupId,
        string idempotencyKey,
        Func<long, Guid, IRequest<TripMate.Application.Common.Models.Result<GetGroupInvitationResponse>>> createCommand,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(idempotencyKey, out var parsedIdempotencyKey)
            || parsedIdempotencyKey == Guid.Empty)
        {
            return Problem(
                title: "A valid Idempotency-Key header is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await Sender.Send(createCommand(currentUserService.UserId.Value, parsedIdempotencyKey), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
