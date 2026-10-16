using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Administrator))]
[Route("api/v1/admin/tours")]
public sealed class AdminToursController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("{tourId:long}/approve")]
    public async Task<IActionResult> Approve(long tourId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ApproveTourCommand(tourId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{tourId:long}/reject")]
    public async Task<IActionResult> Reject(long tourId, [FromBody] RejectTourRequest? request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RejectTourCommand(tourId, request?.Reason ?? string.Empty), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{tourId:long}/publish")]
    public async Task<IActionResult> Publish(long tourId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new PublishTourCommand(tourId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}

public sealed record RejectTourRequest(string? Reason);