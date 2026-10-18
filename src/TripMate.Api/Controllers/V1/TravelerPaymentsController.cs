using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Traveler.Payments.Create;
using TripMate.Application.Features.Traveler.Payments.Cancel;
using TripMate.Application.Features.Traveler.Payments.Mock;
using TripMate.Application.Features.Traveler.Payments.Status;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// UC-28: Make Electronic Payment.
/// Endpoints for payment-link creation/reuse, canonical status check, and mock outcome.
/// </summary>
[Authorize(Roles = nameof(UserRole.Traveler))]
[Route("api/v1/traveler")]
public sealed class TravelerPaymentsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("bookings/{bookingId:long}/payments")]
    [ProducesResponseType(typeof(CreatePaymentLinkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateOrReusePayment(
        long bookingId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new CreateOrReusePaymentLinkCommand(bookingId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("bookings/{bookingId:long}/payment/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelPayment(
        long bookingId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new CancelPaymentLinkCommand(bookingId), cancellationToken);
        return result.IsSuccess ? Ok(new { cancelled = result.Value }) : HandleFailure(result);
    }

    [HttpGet("bookings/{bookingId:long}/payment")]
    [ProducesResponseType(typeof(PaymentStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPaymentStatus(
        long bookingId,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetBookingPaymentStatusQuery(bookingId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("payments/{paymentId:long}/mock-outcome")]
    [ProducesResponseType(typeof(MockPaymentOutcomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteMockOutcome(
        long paymentId,
        [FromBody] MockPaymentOutcomeRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new CompleteMockPaymentCommand(paymentId, request.Outcome),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}

public sealed record MockPaymentOutcomeRequest(string Outcome);
