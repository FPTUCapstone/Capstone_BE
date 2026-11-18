using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Features.Admin.Tours.Review;
using TripMate.Application.Features.Admin.Tours.Review.PendingTours;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Staff))]
[ForbiddenProblemDetails(
    OperatorTourErrorCodes.AdminAccessRequired,
    "You do not have permission to access this function.")]
[Route("api/v1/admin/tours")]
public sealed class AdminToursController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>UC-60 Screen 13: Staff queue of tours waiting for review.</summary>
    [HttpGet("pending")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetPending([FromQuery] GetPendingToursQuery query, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>UC-60 Screen 14: review detail of a Pending tour.</summary>
    [HttpGet("{tourId:long}/review")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetReviewDetail(long tourId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetPendingTourReviewQuery(tourId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{tourId:long}/approve")]
    public async Task<IActionResult> Approve(
        long tourId,
        [FromBody] ApproveTourRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new ApproveTourCommand(tourId, request?.Checklist, request?.Note),
            cancellationToken);
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

    [HttpPost("{tourId:long}/unpublish")]
    public async Task<IActionResult> Unpublish(long tourId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new UnpublishTourCommand(tourId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}

public sealed record ApproveTourRequest(TourReviewChecklistDto? Checklist, string? Note);

public sealed record RejectTourRequest(string? Reason);