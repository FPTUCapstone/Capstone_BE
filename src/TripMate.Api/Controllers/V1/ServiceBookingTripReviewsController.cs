using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Api.Controllers.V1.Requests;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Application.Features.TripReviews.Edit;
using TripMate.Application.Features.TripReviews.GetContext;
using TripMate.Application.Features.TripReviews.Submit;

namespace TripMate.Api.Controllers.V1;

[Authorize(Policy = TripReviewAuthorizationPolicies.ActiveTraveler)]
[AuthorizationProblemDetails(
    TripReviewErrorCodes.Unauthorized,
    "Authentication is required.",
    TripReviewErrorCodes.Forbidden,
    "An active Traveler account is required.")]
[TripReviewValidationExceptionFilter]
[DisableFormValueModelBinding]
[Route("api/v1/service-bookings/{serviceBookingId:long}/review")]
public sealed class ServiceBookingTripReviewsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesErrorResponseType(typeof(void))]
    [ProducesResponseType(typeof(TripReviewContextDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(long serviceBookingId, CancellationToken cancellationToken)
    {
        if (serviceBookingId <= 0) return HandleFailure(InvalidInput());
        var result = await Sender.Send(new GetTripReviewContextQuery(
            ReviewableRecordRef.ServiceBooking(serviceBookingId)), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost]
    [ProducesErrorResponseType(typeof(void))]
    [ProducesResponseType(typeof(NewTripReviewDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Submit(long serviceBookingId, CancellationToken cancellationToken)
    {
        if (serviceBookingId <= 0) return HandleFailure(InvalidInput());
        var request = await TripReviewRequestReader.ReadSubmitAsync(Request, cancellationToken);
        if (request.IsFailure) return HandleFailure(request);
        var value = request.Value;
        var record = ReviewableRecordRef.ServiceBooking(serviceBookingId);
        var result = await Sender.Send(new SubmitTripReviewCommand(record,
            value.OverallRating, value.Title, value.Content, value.PoiRatings, value.Photos,
            value.RoutePacing, value.CspRating, value.PublishDisplayName), cancellationToken);
        return result.IsSuccess
            ? Created($"/api/v1/service-bookings/{serviceBookingId}/review", result.Value)
            : HandleFailure(result);
    }

    [HttpPut]
    [ProducesErrorResponseType(typeof(void))]
    [ProducesResponseType(typeof(NewTripReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Edit(long serviceBookingId, CancellationToken cancellationToken)
    {
        if (serviceBookingId <= 0) return HandleFailure(InvalidInput());
        var request = await TripReviewRequestReader.ReadEditAsync(Request, cancellationToken);
        if (request.IsFailure) return HandleFailure(request);
        var value = request.Value;
        var result = await Sender.Send(new EditTripReviewCommand(
            ReviewableRecordRef.ServiceBooking(serviceBookingId), value.OverallRating,
            value.Title, value.Content, value.PublishDisplayName, value.Version), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    private static Result InvalidInput() =>
        Result.Failure(TripReviewErrorCodes.InvalidInput, "The review request is invalid.");
}