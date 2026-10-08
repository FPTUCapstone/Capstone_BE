using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

using TripMate.Api.Common;
using TripMate.Application.Features.Admin.Users.Unlock;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1;

[Authorize(Roles = nameof(UserRole.Administrator))]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("{userId:long}/unlock")]
    [ProducesResponseType(typeof(UnlockUserAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Unlock(
        long userId,
        [FromBody] UnlockUserAccountRequest request,
        [BindRequired, FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new UnlockUserAccountCommand(userId, request.Reason, idempotencyKey),
            cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : HandleFailure(result);
    }

    public sealed record UnlockUserAccountRequest(string Reason);
}