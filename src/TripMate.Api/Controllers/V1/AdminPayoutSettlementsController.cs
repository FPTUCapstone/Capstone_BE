using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.Users.Common;
using TripMate.Application.Features.Admin.Users.Unlock;
using TripMate.Application.Features.Payouts.DailySettlement;
using TripMate.Application.Features.Payouts.DailySettlement.Admin;
using TripMate.Application.Features.Payouts.DailySettlement.Reads;
using TripMate.Domain.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// DAILY-PAYOUT-TRANSFER (UC-72, UC-73). Mounted at a distinct path from the legacy
/// <c>/api/v1/admin/payouts</c> (the UC-46 payout-request model, scheduled for its own removal
/// decision — see plan Task 0/1) so the two do not collide while both exist in the codebase.
/// </summary>
[Authorize(Roles = "Staff,Administrator")]
[Route("api/v1/admin/payout-settlements")]
public sealed class AdminPayoutSettlementsController(
    ISender sender, ICurrentUserService currentUser, IAuditFailureRecorder failures) : ApiControllerBase(sender)
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] GetPayoutSettlementsQuery query, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpGet("{batchId:long}")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetDetail(long batchId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetFailedPayoutBatchDetailQuery(batchId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    [HttpPost("{batchId:long}/retry")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Retry(long batchId, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RetryFailedPayoutBatchCommand(batchId), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    public sealed record MarkResolvedRequest(string? Reason);

    [HttpPost("{batchId:long}/resolve")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> MarkResolved(
        long batchId, [FromBody] MarkResolvedRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new MarkPayoutBatchResolvedCommand(batchId, request.Reason), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    /// <summary>Staff-only Tour Operator unlock with a recorded reason and the UC-48 settlement checks.</summary>
    [HttpPost("tour-operators/{tourOperatorUserId:long}/unlock")]
    [Authorize(Roles = "Staff")]
    [AuthorizationFailureAudit(AuditActionTypes.UserUnlock, AuditEntityTypes.User, "tourOperatorUserId")]
    [ProducesResponseType<UnlockTourOperatorResponseDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UnlockTourOperator(
        long tourOperatorUserId,
        [FromBody] AccountModerationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await Sender.Send(
            new UnlockUserAccountCommand(tourOperatorUserId, request.Reason, idempotencyKey, UserRole.TourOperator),
            cancellationToken);
        if (result.IsSuccess)
            return Ok(new UnlockTourOperatorResponseDto(
                result.Value.UserId, result.Value.RestoredStatus.ToString(), result.Value.UnlockedAtUtc));

        if (result.ErrorCode is UserAdministrationErrorCodes.Forbidden or UserAdministrationErrorCodes.NotFound)
        {
            await failures.RecordAsync(new AuditFailureEvent(currentUser.UserId, AuditActionTypes.UserUnlock,
                AuditEntityTypes.User, null, result.ErrorCode));
        }

        var errorCode = result.ErrorCode switch
        {
            UserAdministrationErrorCodes.NotFound => DailyPayoutErrorCodes.OperatorNotFound,
            UserAdministrationErrorCodes.NotLocked => DailyPayoutErrorCodes.OperatorNotLocked,
            _ => result.ErrorCode!,
        };
        return HandleFailure(Result.Failure(errorCode, result.ErrorMessage!, result.ErrorMetadata));
    }

    public sealed record UnlockTourOperatorResponseDto(long TourOperatorUserId, string Status, DateTimeOffset UpdatedAtUtc);
}
