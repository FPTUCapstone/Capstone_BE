using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripMate.Api.Common;
using TripMate.Application.Features.Admin.TourOperatorApplications.Approve;
using TripMate.Application.Features.Admin.TourOperatorApplications.GetDetail;
using TripMate.Application.Features.Admin.TourOperatorApplications.Reject;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = "Administrator")]
[Route("api/v1/admin/tour-operator-applications")]
public class AdminTourOperatorApplicationsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("{userId:long}")]
    public async Task<IActionResult> GetDetail(long userId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetOperatorApplicationDetailQuery(userId), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{userId:long}/approve")]
    public async Task<IActionResult> Approve(long userId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ApproveOperatorApplicationCommand(userId), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{userId:long}/reject")]
    public async Task<IActionResult> Reject(
        long userId,
        [FromBody] RejectTourOperatorApplicationRequest? request,
        CancellationToken cancellationToken)
    {
        var reason = request?.Reason ?? string.Empty;
        var result = await Sender.Send(
            new RejectOperatorApplicationCommand(userId, reason),
            cancellationToken);

        return result.IsSuccess ? Ok() : HandleFailure(result);
    }
}

public record RejectTourOperatorApplicationRequest(string? Reason);
