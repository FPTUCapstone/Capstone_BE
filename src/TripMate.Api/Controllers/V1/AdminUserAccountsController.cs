using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
public sealed class AdminUserAccountsController(ISender sender) : ApiControllerBase(sender)
{
    public const string UserIdRouteValueName = "userId";

    [HttpGet]
    [ForbiddenProblemDetails(UserAccountErrors.Forbidden, "Access denied.")]
    [ProducesResponseType<UserAccountPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ListUserAccountsQuery query, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(query, ct);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{userId:long}")]
    [ForbiddenProblemDetails(UserAccountErrors.Forbidden, "Access denied.")]
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
            if (!User.IsInRole(nameof(UserRole.Administrator)))
            {
                var actorIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                long.TryParse(actorIdStr, NumberStyles.None, CultureInfo.InvariantCulture, out var actorId);
                var recorder = HttpContext.RequestServices.GetService<IAuditFailureRecorder>();
                if (recorder != null)
                {
                    await recorder.RecordAsync(new AuditFailureEvent(
                        actorId > 0 ? actorId : null,
                        AuditActionTypes.UserUnlock,
                        AuditEntityTypes.User,
                        userId > 0 ? userId : null,
                        AuthorizationAuditErrorCodes.Forbidden));
                }

                return Forbid();
            }

            var result = await Sender.Send(
                new UnlockUserAccountCommand(userId, request.Reason, idempotencyKey),
                ct);
            return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
        }

        if (User.IsInRole(nameof(UserRole.Administrator)))
        {
            var db = HttpContext.RequestServices.GetRequiredService<IApplicationDbContext>();
            var targetRole = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => (UserRole?)u.Role)
                .SingleOrDefaultAsync(ct);

            if (targetRole != UserRole.Staff)
            {
                var problem = new ValidationProblemDetails(new Dictionary<string, string[]>
                {
                    ["Idempotency-Key"] = ["The Idempotency-Key header is required."]
                })
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred."
                };
                problem.Extensions["errorCode"] = UserAdministrationErrorCodes.RequestInvalid;
                return BadRequest(problem);
            }
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
