using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using TripMate.Api.Common;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.PasswordReset;

namespace TripMate.Api.Controllers.V1;

public record PasswordResetRequestDto(string Email);

public record PasswordResetConfirmDto(string Email, string Code, string NewPassword);

/// <summary>
/// UC-06 password-reset endpoints. Both are anonymous and account-independent: the request
/// endpoint returns the exact same generic payload for every account-specific outcome, and
/// the confirm endpoint maps invalid resets to 400 ProblemDetails with MSG14 (the shared
/// HandleFailure mapping sends MSG14 to 401 for the authenticated auth flows — that is not
/// the UC-06 contract) and unexpected system failures to 5xx with MSG127. Per-IP rate
/// limiting guards both endpoints without revealing anything account-specific.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(PasswordResetRateLimiter.PolicyName)]
[Route("api/v1/auth/password-reset")]
public sealed class PasswordResetController(ISender sender) : ApiControllerBase(sender)
{
    [HttpPost("request")]
    [ProducesResponseType(typeof(RequestPasswordResetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestReset(
        [FromBody] PasswordResetRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RequestPasswordResetCommand(request.Email), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : HandleFailure(result);
    }

    [HttpPost("confirm")]
    [ProducesResponseType(typeof(ConfirmPasswordResetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ConfirmReset(
        [FromBody] PasswordResetConfirmDto request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new ConfirmPasswordResetCommand(request.Email, request.Code, request.NewPassword),
            cancellationToken);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var statusCode = result.ErrorCode == AuthErrorCodes.Msg127
            ? StatusCodes.Status500InternalServerError
            : StatusCodes.Status400BadRequest;

        return Problem(
            title: result.ErrorMessage,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = result.ErrorCode,
            });
    }
}