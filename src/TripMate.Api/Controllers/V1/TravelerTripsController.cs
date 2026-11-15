using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripHistory.Common;
using TripMate.Application.Features.TripHistory.Get;
using TripMate.Application.Features.TripHistory.List;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// UC-32 View Trip History: the signed-in Traveler's own tour bookings and self-planned itineraries,
/// grouped Upcoming / Completed / Cancelled. Read-only.
/// </summary>
[Authorize(Policy = TripReviewAuthorizationPolicies.ActiveTraveler)]
[AuthorizationProblemDetails(
    TripHistoryErrorCodes.Unauthorized,
    "Authentication is required.",
    TripHistoryErrorCodes.Forbidden,
    "An active Traveler account is required.")]
[Route("api/v1/traveler/trips")]
public sealed class TravelerTripsController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType(typeof(TripHistoryPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> List(
        [FromQuery] string? tab,
        [FromQuery] string? tripType,
        [FromQuery] string? fromDate,
        [FromQuery] string? toDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = ListTravelerTripsQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return HandleFailure(NotSignedIn());
        }

        var result = await Sender.Send(
            new ListTravelerTripsQuery(
                currentUserService.UserId.Value, tab, tripType, fromDate, toDate, page, pageSize),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <param name="kind"><c>commerceBooking</c> or <c>itinerary</c>; an id is meaningful only with its kind.</param>
    /// <param name="id">
    /// Taken as text so a non-numeric or oversized id reaches the validator and gets the stable
    /// <c>trip_history.invalid_record</c> code instead of a route miss.
    /// </param>
    [HttpGet("{kind}/{id}")]
    [ProducesResponseType(typeof(TripDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Get(string kind, string id, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return HandleFailure(NotSignedIn());
        }

        if (!long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var recordId))
        {
            return HandleFailure(Result.Failure(
                TripHistoryErrorCodes.InvalidRecord,
                "The record id must be a positive whole number."));
        }

        var result = await Sender.Send(
            new GetTravelerTripQuery(currentUserService.UserId.Value, kind, recordId),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    private static Result NotSignedIn() =>
        Result.Failure(TripHistoryErrorCodes.Unauthorized, "Authentication is required.");
}