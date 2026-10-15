using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Traveler.Profile.Common;
using TripMate.Application.Features.Traveler.Profile.Get;
using TripMate.Application.Features.Traveler.Profile.Update;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/traveler/profile")]
public sealed class TravelerProfileController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(TravelerProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetTravelerProfileQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    [ProducesResponseType(typeof(UpdateTravelerProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        [FromForm] UpdateTravelerProfileRequest request,
        CancellationToken cancellationToken)
    {
        byte[]? avatarBytes = null;
        if (request.Avatar is not null)
        {
            if (request.Avatar.Length is <= 0 or > UpdateTravelerProfileCommandValidator.MaxAvatarBytes)
                return Problem(title: "Avatar is invalid.", statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = TravelerProfileErrorCodes.InvalidAvatar });
            await using var stream = request.Avatar.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            avatarBytes = buffer.ToArray();
        }
        var result = await Sender.Send(new UpdateTravelerProfileCommand(
            request.FullName ?? string.Empty,
            request.PhoneNumber,
            request.DateOfBirth,
            avatarBytes,
            request.Avatar?.ContentType,
            request.RemoveAvatar), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}