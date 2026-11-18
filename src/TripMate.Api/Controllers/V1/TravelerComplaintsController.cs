using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Features.Complaints.PartialRefundProposals;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>UC-75 Respond to Partial Refund Proposal (screen #97).</summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[AuthorizationProblemDetails(
    PartialRefundProposalErrorCodes.Unauthorized,
    "Authentication is required.",
    PartialRefundProposalErrorCodes.Forbidden,
    "A Traveler account is required.")]
[Route("api/v1/traveler/complaints")]
public sealed class TravelerComplaintsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet("{complaintId:long}/partial-refund-proposal")]
    [ProducesResponseType(typeof(PartialRefundProposalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPartialRefundProposal(long complaintId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetPartialRefundProposalQuery(complaintId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{complaintId:long}/partial-refund-proposal/response")]
    [ProducesResponseType(typeof(RespondToPartialRefundProposalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RespondToPartialRefundProposal(long complaintId,
        RespondToPartialRefundProposalRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new RespondToPartialRefundProposalCommand(complaintId, request.Decision!.Value, request.Reason,
                request.RequestedAmount), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}