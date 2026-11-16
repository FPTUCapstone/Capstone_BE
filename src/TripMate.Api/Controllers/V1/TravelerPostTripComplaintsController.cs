using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Application.Features.Traveler.PostTripComplaints.AmendEvidence;
using TripMate.Application.Features.Traveler.PostTripComplaints.DownloadEvidence;
using TripMate.Application.Features.Traveler.PostTripComplaints.GetContext;
using TripMate.Application.Features.Traveler.PostTripComplaints.List;
using TripMate.Application.Features.Traveler.PostTripComplaints.Submit;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-68 — submit post-trip complaint with private evidence.</summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/traveler/bookings")]
public sealed class TravelerPostTripComplaintsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("/api/v1/traveler/post-trip-complaints")]
    [ProducesResponseType(typeof(IReadOnlyList<MyPostTripComplaintResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMine(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ListMyPostTripComplaintsQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{bookingId:long}/post-trip-complaint")]
    [ProducesResponseType(typeof(PostTripComplaintContextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetContext(long bookingId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetPostTripComplaintContextQuery(bookingId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{bookingId:long}/post-trip-complaints")]
    [RequestSizeLimit(31 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(SubmitPostTripComplaintResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(SubmitPostTripComplaintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Submit(long bookingId,
        [FromForm] string? reasonCategory,
        [FromForm] string? reasonDetail,
        [FromForm(Name = "evidence")] List<IFormFile>? evidence,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out Guid key))
            return Problem(title: "A valid Idempotency-Key header is required.", statusCode: StatusCodes.Status400BadRequest);
        if (!Enum.TryParse(reasonCategory, ignoreCase: true, out PostTripComplaintReasonCategory category)
            || !Enum.IsDefined(category))
            return Problem(title: "A valid reason category is required.", statusCode: StatusCodes.Status400BadRequest);

        var uploads = new List<ComplaintEvidenceUpload>();
        if ((evidence?.Count ?? 0) > 5)
            return Problem(title: "At most five evidence files are allowed.", statusCode: StatusCodes.Status422UnprocessableEntity);
        long totalBytes = 0;
        foreach (IFormFile file in evidence ?? [])
        {
            if (file.Length <= 0 || file.Length > 10L * 1024 * 1024)
                return Problem(title: "An evidence file exceeds the permitted size.", statusCode: StatusCodes.Status422UnprocessableEntity);
            totalBytes += file.Length;
            if (totalBytes > 30L * 1024 * 1024)
                return Problem(title: "Evidence exceeds the permitted total size.", statusCode: StatusCodes.Status422UnprocessableEntity);
            await using Stream stream = file.OpenReadStream();
            using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, cancellationToken);
            uploads.Add(new ComplaintEvidenceUpload(file.FileName, file.ContentType, buffer.ToArray()));
        }

        var result = await Sender.Send(new SubmitPostTripComplaintCommand(bookingId, key, category,
            reasonDetail ?? string.Empty, uploads), cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return result.Value.IsReplay ? Ok(result.Value) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpPost("{bookingId:long}/post-trip-complaint/evidence")]
    [RequestSizeLimit(31 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(AmendPostTripComplaintEvidenceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AmendPostTripComplaintEvidenceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AmendEvidence(long bookingId,
        [FromForm(Name = "evidence")] List<IFormFile>? evidence,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out Guid key))
            return Problem(title: "A valid Idempotency-Key header is required.", statusCode: StatusCodes.Status400BadRequest);
        if ((evidence?.Count ?? 0) == 0 || (evidence?.Count ?? 0) > 5)
            return Problem(title: "One to five evidence files are required.", statusCode: StatusCodes.Status422UnprocessableEntity);
        var uploads = new List<ComplaintEvidenceUpload>();
        long totalBytes = 0;
        foreach (IFormFile file in evidence ?? [])
        {
            if (file.Length <= 0 || file.Length > 10L * 1024 * 1024)
                return Problem(title: "An evidence file exceeds the permitted size.", statusCode: StatusCodes.Status422UnprocessableEntity);
            totalBytes += file.Length;
            if (totalBytes > 30L * 1024 * 1024)
                return Problem(title: "Evidence exceeds the permitted total size.", statusCode: StatusCodes.Status422UnprocessableEntity);
            await using Stream stream = file.OpenReadStream();
            using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, cancellationToken);
            uploads.Add(new ComplaintEvidenceUpload(file.FileName, file.ContentType, buffer.ToArray()));
        }
        var result = await Sender.Send(new AmendPostTripComplaintEvidenceCommand(bookingId, key, uploads), cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return result.Value.IsReplay ? Ok(result.Value) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpGet("{bookingId:long}/post-trip-complaint/evidence/{evidenceId:long}/download")]
    public async Task<IActionResult> DownloadEvidence(long bookingId, long evidenceId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new DownloadPostTripComplaintEvidenceQuery(bookingId, evidenceId), cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return result.Value.TemporaryUrl is not null
            ? Redirect(result.Value.TemporaryUrl.ToString())
            : File(result.Value.Bytes!, result.Value.ContentType, result.Value.FileName);
    }
}
