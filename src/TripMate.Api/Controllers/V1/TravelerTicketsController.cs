using System.Globalization;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Tickets.Common;
using TripMate.Application.Features.Tickets.Get;
using TripMate.Application.Features.Tickets.Refresh;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// UC-29 View QR E-ticket for the signed-in Traveler's own bookings. Contract:
/// docs/UC-29-api-contract.md. Ids are taken as text so a non-positive or non-numeric id is a 400,
/// not a route miss.
/// </summary>
[Authorize(Policy = TripReviewAuthorizationPolicies.ActiveTraveler)]
[AuthorizationProblemDetails(
    TicketErrorCodes.Unauthorized,
    "Authentication is required.",
    TicketErrorCodes.Forbidden,
    "An active Traveler account is required.")]
[Route("api/v1/traveler")]
public sealed class TravelerTicketsController(
    ISender sender,
    ICurrentUserService currentUserService) : ApiControllerBase(sender)
{
    /// <summary>Trip History → [View E-ticket] with <c>actions.ticketId</c>.</summary>
    [HttpGet("tickets/{ticketId}")]
    [ProducesResponseType(typeof(TicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> GetTicket(string ticketId, CancellationToken cancellationToken) =>
        SendAsync(ticketId, (travelerId, id) => GetTravelerTicketQuery.ForTicket(travelerId, id), cancellationToken);

    /// <summary>Booking Confirmation screen, which knows only the booking id.</summary>
    [HttpGet("bookings/{bookingId}/ticket")]
    [ProducesResponseType(typeof(TicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> GetBookingTicket(string bookingId, CancellationToken cancellationToken) =>
        SendAsync(bookingId, (travelerId, id) => GetTravelerTicketQuery.ForBooking(travelerId, id), cancellationToken);

    /// <summary>[Refresh QR Code]: the previous QR stops working at check-in.</summary>
    [HttpPost("tickets/{ticketId}/qr-code/refresh")]
    [ProducesResponseType(typeof(TicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorCodeProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> RefreshQrCode(string ticketId, CancellationToken cancellationToken) =>
        SendAsync(ticketId, (travelerId, id) => new RefreshTicketQrCommand(travelerId, id), cancellationToken);

    private async Task<IActionResult> SendAsync(string rawId,
        Func<long, long, IRequest<Result<TicketResponse>>> createRequest, CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return HandleFailure(Result.Failure(TicketErrorCodes.Unauthorized, "Authentication is required."));
        }

        if (!long.TryParse(rawId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return Problem(title: "The id must be a positive whole number.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await Sender.Send(createRequest(currentUserService.UserId.Value, id), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}
