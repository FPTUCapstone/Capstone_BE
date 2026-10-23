using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.Admin.LandingContent;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-66 authoring API. The public landing page reads published snapshots only.</summary>
[Authorize(Roles = nameof(UserRole.Administrator) + "," + nameof(UserRole.Staff))]
[Route("api/v1/admin/landing-content")]
public sealed class AdminLandingContentController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetLandingContentQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("draft")]
    public async Task<IActionResult> SaveDraft([FromBody] SaveLandingContentDraftCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("publish")]
    public async Task<IActionResult> Publish([FromBody] PublishLandingContentDraftCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("restore")]
    public async Task<IActionResult> Restore([FromBody] RestoreLandingContentVersionCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("approved-tours")]
    public async Task<IActionResult> GetApprovedTours(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetLandingContentApprovedToursQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("media")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromForm] IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null) return Problem(title: "An image file is required.", statusCode: StatusCodes.Status400BadRequest);
        await using var stream = file.OpenReadStream();
        var result = await Sender.Send(new UploadLandingContentMediaCommand(new TourMediaImageSource(
            stream, file.Length, file.FileName, file.ContentType)), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}