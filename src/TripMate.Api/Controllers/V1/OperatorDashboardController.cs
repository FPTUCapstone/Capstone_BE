using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Operator.Dashboard;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Policy = global::TripMate.Api.Authorization.OperatorContractWorkspacePolicies.Workspace)]
[Authorize(Roles = nameof(UserRole.TourOperator))]
[Route("api/v1/operator/dashboard")]
public sealed class OperatorDashboardController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("metrics")]
    [ProducesResponseType(typeof(OperatorDashboardMetricsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Metrics(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorDashboardMetricsQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}