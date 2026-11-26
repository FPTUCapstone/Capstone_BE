using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using TripMate.Api.Common;
using TripMate.Application.Features.Weather.PublicForecast;

namespace TripMate.Api.Controllers.V1;

[ApiController]
[AllowAnonymous]
[Route("api/v1/weather/forecast")]
[EnableRateLimiting(PublicWeatherRateLimiter.PolicyName)]
public sealed class WeatherForecastController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// The read-only forecast of a place in Vietnam for the public weather page. Coordinates are rounded to two
    /// decimals; <c>current</c> is the forecast for the current hour. 503 when the weather service is off, failing or
    /// out of quota.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PublicWeatherForecastResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(
        [FromQuery(Name = "lat")] decimal latitude,
        [FromQuery(Name = "lon")] decimal longitude,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetPublicWeatherForecastQuery(latitude, longitude), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}