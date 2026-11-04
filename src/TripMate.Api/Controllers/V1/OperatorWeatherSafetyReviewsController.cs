using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripMate.Api.Common;
using TripMate.Application.Features.Operator.WeatherSafetyReviews;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/weather-safety-reviews")]
public sealed class OperatorWeatherSafetyReviewsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WeatherSafetyReviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetWeatherSafetyReviewsQuery(status), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{reviewId:long}")]
    [ProducesResponseType(typeof(WeatherSafetyReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        long reviewId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetWeatherSafetyReviewByIdQuery(reviewId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{reviewId:long}/acknowledge")]
    [ProducesResponseType(typeof(WeatherSafetyReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Acknowledge(
        long reviewId,
        [FromBody] AcknowledgeWeatherSafetyReviewRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new AcknowledgeWeatherSafetyReviewCommand(reviewId, request?.ImmediateInstructions), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}

public sealed record AcknowledgeWeatherSafetyReviewRequest(string? ImmediateInstructions);
