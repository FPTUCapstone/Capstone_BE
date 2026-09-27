using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Media;
using TripMate.Application.Features.TourMedia.Common;
using TripMate.Application.Features.TourMedia.Delete;
using TripMate.Application.Features.TourMedia.List;
using TripMate.Application.Features.TourMedia.Reorder;
using TripMate.Application.Features.TourMedia.UpdateMetadata;
using TripMate.Application.Features.TourMedia.Upload;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/tours/{tourId:long}/media")]
public sealed class OperatorTourMediaController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TourMediaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(long tourId, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        var result = await Sender.Send(new ListTourMediaQuery(tourId, currentUserService.UserId.Value), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
    [ProducesResponseType(typeof(TourMediaDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Upload(long tourId, [FromForm] UploadTourMediaRequest request,
        [BindRequired, FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty || request.File is null)
            return Problem(title: "A file and valid Idempotency-Key header are required.", statusCode: StatusCodes.Status400BadRequest);
        await using var stream = request.File.OpenReadStream();
        var result = await Sender.Send(new UploadTourMediaCommand(tourId, currentUserService.UserId.Value, key,
            new TourMediaImageSource(stream, request.File.Length, request.File.FileName, request.File.ContentType),
            request.Caption, request.AltText, request.IsPrimary), cancellationToken);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : HandleFailure(result);
    }

    [HttpPatch("{mediaId:long}")]
    [ProducesResponseType(typeof(TourMediaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateMetadata(long tourId, long mediaId,
        [FromBody] UpdateTourMediaMetadataRequest request, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        var result = await Sender.Send(new UpdateTourMediaMetadataCommand(tourId, mediaId,
            currentUserService.UserId.Value, request.Caption, request.AltText), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPut("order")]
    [ProducesResponseType(typeof(IReadOnlyList<TourMediaDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reorder(long tourId, [FromBody] ReorderTourMediaRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        var result = await Sender.Send(new ReorderTourMediaCommand(tourId, currentUserService.UserId.Value,
            request.MediaIds, request.PrimaryMediaId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpDelete("{mediaId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(long tourId, long mediaId, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue) return Unauthorized();
        var result = await Sender.Send(new DeleteTourMediaCommand(tourId, mediaId, currentUserService.UserId.Value), cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }
}