using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.UpcomingTrips;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>Upcoming Trip Weather: the personal layer of the public /weather page.</summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/traveler/upcoming-trip-weather")]
[EnableRateLimiting(PublicWeatherRateLimiter.PolicyName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TravelerTripWeatherController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(UpcomingTripWeatherResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(new GetUpcomingTripWeatherQuery(currentUserService.UserId.Value),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}