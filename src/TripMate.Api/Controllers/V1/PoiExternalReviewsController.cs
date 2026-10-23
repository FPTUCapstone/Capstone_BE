using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.PointsOfInterest.ExternalReviews;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// Tripadvisor rating, recent reviews and on-demand translation of a POI. Signed-in travelers only, so
/// anonymous traffic cannot spend the shared Tripadvisor quota. Clients load this after the page renders.
/// </summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/pois/{poiId:long}/external-reviews")]
public sealed class PoiExternalReviewsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(PoiExternalReviewsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        long poiId,
        [FromQuery] int size = GetPoiExternalReviewsQuery.DefaultSize,
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new GetPoiExternalReviewsQuery(poiId, size), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{reviewId:long}/translation")]
    [ProducesResponseType(typeof(TranslatedExternalReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Translate(
        long poiId,
        long reviewId,
        [FromQuery] string target = TranslateExternalReviewQuery.Vietnamese,
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new TranslateExternalReviewQuery(poiId, reviewId, target), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}