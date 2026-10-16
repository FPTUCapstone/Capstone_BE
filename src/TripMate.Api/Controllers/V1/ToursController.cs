using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Tours.GetDetail;
using TripMate.Application.Features.Tours.Search;

namespace TripMate.Api.Controllers.V1;

[AllowAnonymous]
[Route("api/v1/tours")]
public sealed class ToursController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesErrorResponseType(typeof(void))]
    [ProducesResponseType(typeof(PagedToursResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Search(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!TourSearchQueryParser.TryParse(Request.Query, ModelState, out var query))
        {
            return ValidationProblem(ModelState);
        }

        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>UC-26: one publicly visible tour. A hidden or missing tour is an identical 404 (MSG65).</summary>
    [HttpGet("{tourId:long}")]
    [ProducesResponseType(typeof(TourDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDetail(long tourId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await Sender.Send(new GetTourDetailQuery(tourId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}