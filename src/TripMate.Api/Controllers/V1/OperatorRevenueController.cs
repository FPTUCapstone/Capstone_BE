using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.Revenue.GetReport;
using TripMate.Application.Features.Operator.Revenue.GetTours;
using TripMate.Domain.Enums;
namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/revenue")]
public sealed class OperatorRevenueController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("report")]
    public async Task<IActionResult> Get([FromQuery] DateOnly startDate, [FromQuery] DateOnly endDate, [FromQuery] string granularity = "monthly", [FromQuery] long? tourId = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    { var result = await Sender.Send(new GetOperatorRevenueReportQuery(startDate, endDate, granularity, tourId, page, pageSize), cancellationToken); return result.IsSuccess ? Ok(result.Value) : HandleFailure(result); }

    [HttpGet("tours")]
    public async Task<IActionResult> Tours(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorRevenueToursQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}