using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Operator.PostTripComplaints.Respond;
using TripMate.Application.Features.Operator.PostTripComplaints.Queries;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-69 — Tour Operator response to an owned post-trip complaint.</summary>
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/post-trip-complaints")]
public sealed class OperatorPostTripComplaintsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(OperatorPostTripComplaintPageResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PostTripComplaintStatus? status, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new ListOperatorPostTripComplaintsQuery(status, page, pageSize), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{complaintId:long}")]
    [ProducesResponseType(typeof(OperatorPostTripComplaintDetailResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Detail(long complaintId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorPostTripComplaintDetailQuery(complaintId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{complaintId:long}/evidence/{evidenceId:long}/download")]
    public async Task<IActionResult> DownloadEvidence(long complaintId, long evidenceId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new DownloadOperatorPostTripComplaintEvidenceQuery(complaintId, evidenceId),
            cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return result.Value.TemporaryUrl is not null
            ? Redirect(result.Value.TemporaryUrl.ToString())
            : File(result.Value.Bytes!, result.Value.ContentType, result.Value.FileName);
    }

    [HttpPost("{complaintId:long}/response")]
    [ProducesResponseType(typeof(RespondToPostTripComplaintResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(RespondToPostTripComplaintResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Respond(long complaintId, RespondToPostTripComplaintRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out Guid key))
            return Problem(title: "A valid Idempotency-Key header is required.", statusCode: StatusCodes.Status400BadRequest);
        if (request.Action is null || !Enum.IsDefined(request.Action.Value))
            return Problem(title: "A valid complaint response action is required.", statusCode: StatusCodes.Status400BadRequest);

        var result = await Sender.Send(new RespondToPostTripComplaintCommand(complaintId, key, request.Action.Value,
            request.ProposedRefundAmount, request.ProposalReason, request.RejectionReason), cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        return result.Value.IsReplay ? Ok(result.Value) : StatusCode(StatusCodes.Status201Created, result.Value);
    }
}
