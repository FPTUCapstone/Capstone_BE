using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Complaints.StaffReview;
using TripMate.Application.Features.Traveler.PostTripComplaints.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler) + "," + nameof(UserRole.TourOperator))]
[Route("api/v1/traveler/complaints")]
[Route("api/v1/operator/post-trip-complaints")]
public sealed class PartyComplaintStaffReviewController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("{id:long}/staff-review")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new GetStaffComplaintQuery(id, true), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/staff-review/evidence/{evidenceId:long}")]
    public async Task<IActionResult> Evidence(long id, long evidenceId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new DownloadStaffComplaintEvidenceQuery(id, evidenceId, true), cancellationToken);
        if (result.IsFailure) return HandleFailure(result);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(result.Value.Bytes, result.Value.ContentType, result.Value.FileName);
    }

    [HttpPost("{id:long}/staff-evidence")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public Task<IActionResult> Submit(long id, [FromForm] PartyStaffEvidenceRequest request, CancellationToken cancellationToken)
        => SubmitCore(id, request, false, cancellationToken);

    [HttpPost("{id:long}/reconsideration")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public Task<IActionResult> Reconsider(long id, [FromForm] PartyStaffEvidenceRequest request, CancellationToken cancellationToken)
        => SubmitCore(id, request, true, cancellationToken);

    private async Task<IActionResult> SubmitCore(long id, PartyStaffEvidenceRequest request, bool reconsideration, CancellationToken ct)
    {
        if (request.Files.Count is < 1 or > StaffComplaintEvidenceRules.MaximumFiles || request.Files.Any(f => f.Length > StaffComplaintEvidenceRules.MaximumBytes))
            return BadRequest(new ProblemDetails { Title = "Supply one to three evidence files of at most 5 MB each." });
        List<ComplaintEvidenceUpload> files = [];
        foreach (var file in request.Files)
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, ct);
            files.Add(new ComplaintEvidenceUpload(Path.GetFileName(file.FileName), file.ContentType, stream.ToArray()));
        }
        var result = await Sender.Send(new SubmitStaffComplaintEvidenceCommand(id, request.OperationId, request.RequestEventId,
            request.Reason, files, reconsideration), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}

public sealed class PartyStaffEvidenceRequest
{
    public Guid OperationId { get; set; }
    public long? RequestEventId { get; set; }
    public string Reason { get; set; } = "";
    public List<IFormFile> Files { get; set; } = [];
}