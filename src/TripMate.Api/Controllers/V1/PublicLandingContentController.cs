using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Public.LandingContent;

namespace TripMate.Api.Controllers.V1;

/// <summary>Anonymous home-page read API. It returns only the latest published CMS snapshot.</summary>
[AllowAnonymous]
[Route("api/v1/landing-content")]
public sealed class PublicLandingContentController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetPublishedLandingContentQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}