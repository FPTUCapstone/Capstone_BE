using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Bookings.DirectRefunds.Dispute;
using TripMate.Application.Features.Bookings.DirectRefunds.Evidence;
using TripMate.Application.Features.Bookings.DirectRefunds.Get;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>Admin dispute resolution for direct refunds (spec §10, DirectRefundDisputeReview policy).</summary>
[Authorize(Roles = nameof(UserRole.Administrator))]
[Route("api/v1/admin/direct-refund-disputes")]
public sealed class AdminDirectRefundDisputesController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(DirectRefundCaseListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? status = null,
        [FromQuery] int pageNumber = ListDirectRefundCasesQuery.DefaultPageNumber,
        [FromQuery] int pageSize = ListDirectRefundCasesQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new ListDirectRefundCasesQuery(status, pageNumber, pageSize), cancellationToken);
        Response.Headers.CacheControl = "no-store, max-age=0";
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{id:long}/evidence/{evidenceId:long}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Evidence(long id, long evidenceId, [FromQuery] bool download = false,
        CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new DownloadDirectRefundEvidenceQuery(id, evidenceId), cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);
        var evidence = result.Value!;
        Response.Headers.CacheControl = "no-store, max-age=0";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (download) return File(evidence.Bytes, evidence.ContentType, evidence.FileName);
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(evidence.FileName);
        Response.Headers.ContentDisposition = disposition.ToString();
        return File(evidence.Bytes, evidence.ContentType);
    }

    /// <summary>GET /api/v1/admin/direct-refund-disputes/{id}</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(DirectRefundCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetDirectRefundCaseQuery(id), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>POST /api/v1/admin/direct-refund-disputes/{id}/resolve</summary>
    [HttpPost("{id:long}/resolve")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resolve(
        long id,
        [FromBody] ResolveDisputeRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseRowVersion(request.ExpectedVersion, out byte[] rowVersion))
            return Problem(title: "ExpectedVersion must be a valid base-64 string.", statusCode: StatusCodes.Status400BadRequest);

        var command = new ResolveDirectRefundDisputeCommand(id, request.Resolution, request.Reason, rowVersion);
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }

    private static bool TryParseRowVersion(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(base64)) return false;
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record ResolveDisputeRequest(string Resolution, string Reason, string ExpectedVersion);