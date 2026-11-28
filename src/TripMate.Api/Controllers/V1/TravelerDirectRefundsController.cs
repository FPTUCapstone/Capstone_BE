using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.DirectRefunds.Get;
using TripMate.Application.Features.Bookings.DirectRefunds.Recipient;
using TripMate.Application.Features.Bookings.DirectRefunds.TravelerResponse;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>Direct-refund actions available to the Traveler (spec §10).</summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/traveler/direct-refunds")]
public sealed class TravelerDirectRefundsController(ISender sender, IDateTimeProvider clock) : ApiControllerBase(sender)
{
    /// <summary>GET /api/v1/traveler/direct-refunds/by-booking/{bookingId}
    /// Returns a disputed or pending case first, otherwise the latest completed case, for the caller's booking.</summary>
    [HttpGet("by-booking/{bookingId:long}")]
    [ProducesResponseType(typeof(DirectRefundCaseIdResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByBooking(long bookingId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetDirectRefundCaseByBookingQuery(bookingId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>GET /api/v1/traveler/direct-refunds/{id}</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(DirectRefundCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetDirectRefundCaseQuery(id), cancellationToken);
        Response.Headers["X-TripMate-Server-Time"] = clock.UtcNow.ToString("O");
        Response.Headers.CacheControl = "private, no-store";
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>PUT /api/v1/traveler/direct-refunds/{id}/recipient</summary>
    [HttpPut("{id:long}/recipient")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConfirmRecipient(
        long id,
        [FromBody] ConfirmRecipientRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseRowVersion(request.ExpectedVersion, out byte[] rowVersion))
            return Problem(title: "ExpectedVersion must be a valid base-64 string.", statusCode: StatusCodes.Status400BadRequest);

        var command = new ConfirmDirectRefundRecipientCommand(
            id, request.BankCode, request.AccountNumber, request.AccountHolder, rowVersion);
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }

    /// <summary>POST /api/v1/traveler/direct-refunds/{id}/response</summary>
    [HttpPost("{id:long}/response")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Respond(
        long id,
        [FromBody] DirectRefundTravelerResponseRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseRowVersion(request.ExpectedVersion, out byte[] rowVersion))
            return Problem(title: "ExpectedVersion must be a valid base-64 string.", statusCode: StatusCodes.Status400BadRequest);

        var command = new RespondToDirectRefundTransferCommand(
            id, request.TransferId, request.ResponseType, request.ReceivedAmountVnd,
            request.Note, rowVersion);
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : HandleFailure(result);
    }

    private static bool TryParseRowVersion(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(base64)) return false;
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record ConfirmRecipientRequest(
    string BankCode,
    string AccountNumber,
    string AccountHolder,
    string ExpectedVersion);

public sealed record DirectRefundTravelerResponseRequest(
    long TransferId,
    string ResponseType,
    long? ReceivedAmountVnd,
    string? Note,
    string ExpectedVersion);