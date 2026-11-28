using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.OpenApi;
using TripMate.Application.Features.Bookings.DirectRefunds.Evidence;
using TripMate.Application.Features.Bookings.DirectRefunds.Get;
using TripMate.Application.Features.Bookings.DirectRefunds.Transfer;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>Direct-refund actions available to the Tour Operator (spec §10).</summary>
[Authorize(Policy = global::TripMate.Api.Authorization.OperatorContractWorkspacePolicies.Workspace)]
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/direct-refunds")]
public sealed class OperatorDirectRefundsController(ISender sender) : ApiControllerBase(sender)
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

    /// <summary>GET /api/v1/operator/direct-refunds/{id}</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(DirectRefundCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetDirectRefundCaseQuery(id), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>POST /api/v1/operator/direct-refunds/{id}/evidence</summary>
    [IncludeOperationSecurity]
    [HttpPost("{id:long}/evidence")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadDirectRefundEvidenceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UploadEvidence(
        long id,
        [FromForm] UploadEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        var file = request.File;
        if (file is null || file.Length <= 0)
            return Problem(title: "An evidence file is required.", statusCode: StatusCodes.Status400BadRequest);

        await using Stream stream = file.OpenReadStream();
        using var buffer = new MemoryStream((int)file.Length);
        await stream.CopyToAsync(buffer, cancellationToken);

        var command = new UploadDirectRefundEvidenceCommand(id, file.FileName, file.ContentType, buffer.ToArray());
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id }, result.Value)
            : HandleFailure(result);
    }

    /// <summary>POST /api/v1/operator/direct-refunds/{id}/transfers</summary>
    [HttpPost("{id:long}/transfers")]
    [ProducesResponseType(typeof(ReportDirectRefundTransferResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReportTransfer(
        long id,
        [FromBody] ReportTransferRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseRowVersion(request.ExpectedVersion, out byte[] rowVersion))
            return Problem(title: "ExpectedVersion must be a valid base-64 string.", statusCode: StatusCodes.Status400BadRequest);

        var command = new ReportDirectRefundTransferCommand(
            id,
            request.ReportedAmountVnd,
            request.BankReference,
            request.ActualTransferredAtUtc,
            request.EvidenceIds ?? [],
            rowVersion);
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id }, result.Value)
            : HandleFailure(result);
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

public sealed record ReportTransferRequest(
    long ReportedAmountVnd,
    string BankReference,
    DateTimeOffset ActualTransferredAtUtc,
    IReadOnlyList<long>? EvidenceIds,
    string ExpectedVersion);

public sealed class UploadEvidenceRequest
{
    public IFormFile? File { get; set; }
}