using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.RecommendationFeedback.Capture;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/recommendation-feedback")]
public sealed class RecommendationFeedbackController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpPost]
    [ProducesResponseType(typeof(CaptureRecommendationFeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CaptureRecommendationFeedbackResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Capture(
        [FromBody] CaptureRecommendationFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(new CaptureRecommendationFeedbackCommand(
            currentUserService.UserId.Value,
            request.ClientEventId,
            request.EventType,
            request.PoiId,
            request.ItineraryId,
            request.OriginalPosition,
            request.NewPosition,
            request.Source), cancellationToken);

        if (result.IsFailure)
        {
            return HandleFailure(result);
        }

        return StatusCode(
            result.Value.IsReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created,
            result.Value);
    }
}