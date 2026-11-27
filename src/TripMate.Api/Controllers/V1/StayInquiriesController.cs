using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.CommercialServices.Stays;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

public sealed class StayInquiriesController(ISender sender) : ApiControllerBase(sender)
{
    [AllowAnonymous]
    [HttpGet("api/v1/stays")]
    public async Task<IActionResult> Catalog([FromQuery] string? query, [FromQuery] long? beforeId, CancellationToken token) =>
        Respond(await Sender.Send(new ListStayFacilities(null, query, beforeId, false), token));

    [AllowAnonymous]
    [HttpGet("api/v1/stays/{poiId:long:min(1)}")]
    public async Task<IActionResult> Detail(long poiId, CancellationToken token)
    {
        var result = await Sender.Send(new ListStayFacilities(null, null, null, false, poiId), token);
        if (!result.IsSuccess) return Respond(result);
        return result.Value!.Count == 0 ? NotFound() : Ok(result.Value[0]);
    }

    [Authorize(Roles = nameof(UserRole.Traveler))]
    [HttpGet("api/v1/traveler/itineraries/{itineraryId:long:min(1)}/stay-suggestions")]
    public async Task<IActionResult> Suggestions(long itineraryId, CancellationToken token) =>
        Respond(await Sender.Send(new ListStayFacilities(itineraryId, null, null, false), token));

    [Authorize(Roles = nameof(UserRole.Traveler))]
    [HttpPost("api/v1/traveler/stay-inquiries")]
    public async Task<IActionResult> Submit([FromBody] StayInquiryBody body,
        [FromHeader(Name = "Idempotency-Key")] Guid key, CancellationToken token) =>
        Respond(await Sender.Send(new SubmitStayInquiry(body, key), token));

    [Authorize(Roles = nameof(UserRole.Traveler))]
    [HttpGet("api/v1/traveler/stay-inquiries")]
    public async Task<IActionResult> TravelerRequests([FromQuery] long? itineraryId, [FromQuery] long? beforeId, CancellationToken token) =>
        Respond(await Sender.Send(new ListStayInquiries(false, itineraryId, beforeId), token));

    [Authorize(Roles = nameof(UserRole.Traveler))]
    [HttpGet("api/v1/traveler/stay-inquiries/{id:long:min(1)}")]
    public async Task<IActionResult> TravelerRequest(long id, CancellationToken token) =>
        Respond(await Sender.Send(new GetStayInquiry(id, false), token));

    [Authorize(Roles = nameof(UserRole.ServiceProvider))]
    [HttpGet("api/v1/provider/stay-listings")]
    public async Task<IActionResult> ProviderListings([FromQuery] long? beforeId, CancellationToken token) =>
        Respond(await Sender.Send(new ListStayFacilities(null, null, beforeId, true), token));

    [Authorize(Roles = nameof(UserRole.ServiceProvider))]
    [HttpPut("api/v1/provider/stay-listings/{poiId:long:min(1)}")]
    public async Task<IActionResult> Publish(long poiId, [FromBody] ListingBody body, CancellationToken token) =>
        Respond(await Sender.Send(new PublishStayFacility(poiId, body.Description, body.Published, body.AcceptingRequests), token));

    [Authorize(Roles = nameof(UserRole.ServiceProvider))]
    [HttpGet("api/v1/provider/stay-inquiries")]
    public async Task<IActionResult> ProviderRequests([FromQuery] long? beforeId, CancellationToken token) =>
        Respond(await Sender.Send(new ListStayInquiries(true, null, beforeId), token));

    [Authorize(Roles = nameof(UserRole.ServiceProvider))]
    [HttpGet("api/v1/provider/stay-inquiries/{id:long:min(1)}")]
    public async Task<IActionResult> ProviderRequest(long id, CancellationToken token) =>
        Respond(await Sender.Send(new GetStayInquiry(id, true), token));

    [Authorize(Roles = nameof(UserRole.ServiceProvider))]
    [HttpPost("api/v1/provider/stay-inquiries/{id:long:min(1)}/reply")]
    public async Task<IActionResult> Reply(long id, [FromBody] ReplyBody body, CancellationToken token) =>
        Respond(await Sender.Send(new ReplyToStayInquiry(id, body.Declined, body.Reply), token));

    [Authorize(Roles = nameof(UserRole.Traveler) + "," + nameof(UserRole.ServiceProvider))]
    [HttpPost("api/v1/stays/ownership-claims")]
    public async Task<IActionResult> Claim([FromBody] ClaimBody body, CancellationToken token) =>
        Respond(await Sender.Send(new RequestStayOwnership(body.PoiId, body.Evidence), token));

    [Authorize(Roles = nameof(UserRole.Traveler) + "," + nameof(UserRole.ServiceProvider))]
    [HttpGet("api/v1/stays/ownership-claims")]
    public async Task<IActionResult> MyClaims([FromQuery] long? beforeId, CancellationToken token) =>
        Respond(await Sender.Send(new ListStayOwnershipClaims(false, beforeId), token));

    [Authorize(Roles = nameof(UserRole.Staff))]
    [HttpGet("api/v1/admin/stay-ownership-claims")]
    public async Task<IActionResult> Claims([FromQuery] long? beforeId, CancellationToken token) =>
        Respond(await Sender.Send(new ListStayOwnershipClaims(true, beforeId), token));

    [Authorize(Roles = nameof(UserRole.Staff))]
    [HttpPost("api/v1/admin/stay-ownership-claims/{id:long:min(1)}/review")]
    public async Task<IActionResult> ReviewClaim(long id, [FromBody] ReviewClaimBody body, CancellationToken token) =>
        Respond(await Sender.Send(new ReviewStayOwnership(id, body.Approved, body.Note), token));

    private IActionResult Respond<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(result.Value);
        return Problem(title: result.ErrorMessage, statusCode: result.ErrorCode switch
        {
            "Stay.Forbidden" => 403,
            "Stay.NotFound" => 404,
            "Stay.Conflict" => 409,
            _ => 400,
        }, extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });
    }

    public sealed record ListingBody(string? Description, bool Published, bool AcceptingRequests);
    public sealed record ReplyBody(string? Reply, bool Declined);
    public sealed record ClaimBody(long PoiId, string? Evidence);
    public sealed record ReviewClaimBody(bool Approved, string? Note);
}