using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Complaints.StaffReview;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Staff))]
[Route("api/v1/staff/post-trip-complaints")]
public sealed class StaffPostTripComplaintsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new ListStaffComplaintsQuery(page, pageSize, status), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new GetStaffComplaintQuery(id), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{id:long}/decisions")]
    public async Task<IActionResult> Decide(long id, StaffComplaintDecisionRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new DecideStaffComplaintCommand(id, request.Action, request.Reason, request.ContractBasis,
            request.Amount, request.TargetParty, request.ExpectedVersion, request.OperationId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/evidence/{evidenceId:long}")]
    public async Task<IActionResult> Evidence(long id, long evidenceId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new DownloadStaffComplaintEvidenceQuery(id, evidenceId), cancellationToken);
        if (result.IsFailure) return HandleFailure(result);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(result.Value.Bytes, result.Value.ContentType, result.Value.FileName);
    }
}

public sealed record StaffComplaintDecisionRequest(string Action, string Reason, string ContractBasis,
    string ExpectedVersion, Guid OperationId, decimal? Amount = null, string? TargetParty = null);