using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Common;
using TripMate.Application.Features.Operator.Revenue.Exports.Create;
using TripMate.Application.Features.Operator.Revenue.Exports.Download;
using TripMate.Application.Features.Operator.Revenue.Exports.GetHistory;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-45: Export Revenue Report.</summary>
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/revenue/exports")]
public sealed class OperatorRevenueExportsController(ISender sender) : ApiControllerBase(sender)
{
    public sealed record CreateRevenueExportRequest(
        DateOnly StartDate, DateOnly EndDate, string Granularity, long? TourId, string Format, string Scope);

    [HttpPost]
    [ProducesResponseType(typeof(RevenueExportResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(RevenueExportResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Create(
        [FromBody] CreateRevenueExportRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new CreateRevenueExportCommand(
                request.StartDate, request.EndDate, request.Granularity, request.TourId,
                request.Format, request.Scope),
            cancellationToken);

        if (!result.IsSuccess) return HandleFailure(result);

        // 201 Ready when generated synchronously; 202 Queued when left for the worker (Task 5).
        return string.Equals(result.Value.Status, "Ready", StringComparison.Ordinal)
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : StatusCode(StatusCodes.Status202Accepted, result.Value);
    }

    [HttpGet]
    [ProducesResponseType(typeof(RevenueExportHistoryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(new GetRevenueExportHistoryQuery(page, pageSize), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{exportId:long}/download")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(long exportId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetRevenueExportDownloadQuery(exportId), cancellationToken);
        if (!result.IsSuccess) return HandleFailure(result);

        return Redirect(result.Value.DownloadUrl.AbsoluteUri);
    }
}
