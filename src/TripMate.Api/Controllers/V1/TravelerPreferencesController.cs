using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Traveler.Preferences.Common;
using TripMate.Application.Features.Traveler.Preferences.Get;
using TripMate.Application.Features.Traveler.Preferences.GetOptions;
using TripMate.Application.Features.Traveler.Preferences.Update;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-09 Update Travel Preferences (TM-55).</summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/traveler/preferences")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TravelerPreferencesController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpGet("options")]
    [ProducesResponseType(typeof(TravelerPreferenceOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOptions(CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new GetTravelerPreferenceOptionsQuery(currentUserService.UserId.Value),
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet]
    [ProducesResponseType(typeof(TravelerPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new GetTravelerPreferencesQuery(currentUserService.UserId.Value),
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut]
    [ProducesResponseType(typeof(TravelerPreferencesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(
        [FromBody] UpdateTravelerPreferencesApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new UpdateTravelerPreferencesCommand(
                currentUserService.UserId.Value,
                request.Interests,
                request.PreferredTransportMode,
                request.TravelPace,
                request.RiskTolerance,
                request.FoodPreferences),
            cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}