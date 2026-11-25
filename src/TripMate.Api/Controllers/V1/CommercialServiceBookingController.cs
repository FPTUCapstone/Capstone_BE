using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.CommercialServices.Booking;
using TripMate.Application.Features.ServiceProviderRequests;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

public sealed class CommercialServiceBookingController(ISender sender) : ApiControllerBase(sender)
{
    [AllowAnonymous]
    [HttpGet("api/v1/commercial-services/poi/{poiId:long}/options")]
    public async Task<IActionResult> Options(long poiId, CancellationToken token) =>
        Respond(await Sender.Send(new ListBookableServiceOptions(false, poiId), token));

    [Authorize(Roles = nameof(UserRole.ServiceProvider))]
    [HttpGet("api/v1/provider/commercial-services")]
    public async Task<IActionResult> ProviderOptions(CancellationToken token) =>
        Respond(await Sender.Send(new ListBookableServiceOptions(true), token));

    [AllowAnonymous]
    [HttpGet("api/v1/commercial-services/{serviceId:long}/availability")]
    public async Task<IActionResult> Availability(long serviceId, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, CancellationToken token) =>
        Respond(await Sender.Send(new ListCommercialServiceSlots(serviceId, false, from, to), token));

    [Authorize(Roles = nameof(UserRole.ServiceProvider))]
    [HttpGet("api/v1/provider/commercial-services/{serviceId:long}/availability")]
    public async Task<IActionResult> ProviderAvailability(long serviceId, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, CancellationToken token) =>
        Respond(await Sender.Send(new ListCommercialServiceSlots(serviceId, true, from, to), token));

    [Authorize(Roles = nameof(UserRole.ServiceProvider))]
    [HttpPut("api/v1/provider/commercial-services/{serviceId:long}/availability/{startsAt}")]
    public async Task<IActionResult> Publish(long serviceId, DateTimeOffset startsAt,
        [FromBody] PublishAvailabilityBody body, CancellationToken token) =>
        body.Quantity is int quantity
            ? Respond(await Sender.Send(new PublishCommercialServiceSlot(serviceId, startsAt, quantity), token))
            : BadRequest();

    [Authorize(Roles = nameof(UserRole.Traveler))]
    [HttpPost("api/v1/traveler/service-booking-requests")]
    public async Task<IActionResult> Submit([FromBody] SubmitServiceBookingBody body,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey, CancellationToken token) =>
        Respond(await Sender.Send(new SubmitCommercialServiceBooking(body, idempotencyKey), token));

    [Authorize(Roles = nameof(UserRole.Traveler))]
    [HttpGet("api/v1/traveler/service-booking-requests")]
    public async Task<IActionResult> Requests([FromQuery] long? beforeId, CancellationToken token) =>
        Respond(await Sender.Send(new ListTravelerServiceBookings(beforeId), token));

    [Authorize(Roles = nameof(UserRole.Traveler))]
    [HttpPost("api/v1/traveler/service-booking-requests/{bookingId:long:min(1)}/cancel")]
    public async Task<IActionResult> Cancel(long bookingId, CancellationToken token) =>
        Respond(await Sender.Send(new CancelTravelerServiceBooking(bookingId), token));

    private IActionResult Respond<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);
        var status = result.ErrorCode switch
        {
            ProviderRequestErrors.Forbidden => StatusCodes.Status403Forbidden,
            ProviderRequestErrors.NotFound => StatusCodes.Status404NotFound,
            ProviderRequestErrors.InvalidState or CommercialServiceBookingHandler.SlotUnavailable
                or CommercialServiceBookingHandler.QuantityUnavailable
                or CommercialServiceBookingHandler.ServiceUnavailable => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problem(title: result.ErrorMessage, statusCode: status,
            extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });
    }

    public sealed record PublishAvailabilityBody(int? Quantity);
}
