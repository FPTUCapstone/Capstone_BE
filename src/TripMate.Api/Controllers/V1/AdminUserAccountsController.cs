using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Admin.UserAccounts;
using TripMate.Application.Features.Admin.Users.Common;
using TripMate.Application.Features.Admin.Users.Unlock;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Policy = UserRoleExtensions.StaffOrAdministrator)]
[Route("api/v1/admin/users")]
public sealed class AdminUserAccountsController(ISender sender, ICurrentUserService currentUser, IAuditFailureRecorder failures) : ApiControllerBase(sender)
{
    public const string UserIdRouteValueName = "userId";

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
    [AuthorizationFailureAudit(
        AuditActionTypes.UserUnlock,
        AuditEntityTypes.User,
        UserIdRouteValueName)]
    [ProducesResponseType(typeof(UnlockUserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UserAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Unlock(
        long userId,
        [FromBody] AccountModerationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var result = await Sender.Send(
                new UnlockUserAccountCommand(userId, request.Reason, idempotencyKey),
                ct);
            if (result.ErrorCode is UserAdministrationErrorCodes.Forbidden or UserAdministrationErrorCodes.NotFound)
            {
                await failures.RecordAsync(new AuditFailureEvent(currentUser.UserId, AuditActionTypes.UserUnlock,
                    AuditEntityTypes.User, null, result.ErrorCode));
            }

            return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
        }

        return await Moderate(userId, request, false, ct);
    }

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
    [Required, MaxLength(1000)]
    public string Reason { get; init; } = string.Empty;
}