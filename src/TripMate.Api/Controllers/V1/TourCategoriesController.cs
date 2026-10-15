using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Tours.GetCategories;

namespace TripMate.Api.Controllers.V1;

[AllowAnonymous]
[Route("api/v1/tour-categories")]
public sealed class TourCategoriesController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TourCategoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new GetTourCategoriesQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}