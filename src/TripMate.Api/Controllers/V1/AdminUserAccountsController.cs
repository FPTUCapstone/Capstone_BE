using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Admin.UserAccounts;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Policy = UserRoleExtensions.StaffOrAdministrator)]
[Route("api/v1/admin/users")]
public sealed class AdminUserAccountsController(ISender sender) : ApiControllerBase(sender)
{
    [HttpGet]
    [ProducesResponseType<UserAccountPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ListUserAccountsQuery query, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(query, ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{userId:long}")]
    [ProducesResponseType<UserAccountDetailDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Detail(long userId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new GetUserAccountQuery(userId), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{userId:long}/lock")]
    [ProducesResponseType<UserAccountDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Lock(long userId, AccountModerationRequest request, CancellationToken ct) => Moderate(userId, request, true, ct);

    [HttpPost("{userId:long}/unlock")]
    [ProducesResponseType<UserAccountDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> Unlock(long userId, AccountModerationRequest request, CancellationToken ct) => Moderate(userId, request, false, ct);

    private async Task<IActionResult> Moderate(long userId, AccountModerationRequest request, bool locked, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(new ModerateUserAccountCommand(userId, locked, request.Reason), ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AccountModerationRequest
{
    [Required, MaxLength(200)]
    public string Reason { get; init; } = string.Empty;
}