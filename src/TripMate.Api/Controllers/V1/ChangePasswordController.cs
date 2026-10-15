using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Authentication.ChangePassword;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Api.Controllers.V1;

/// <summary>
/// UC-07 Change Password. A separate controller because <see cref="AuthController"/> carries a
/// class-level [AllowAnonymous], which an action-level [Authorize] cannot override in
/// ASP.NET Core. Any authenticated local-password account may change only its own password
/// (self-service; no role restriction — spec §2 SRS_INTERNAL_CONFLICT).
/// </summary>
[Authorize]
[Route("api/v1/auth")]
public sealed class ChangePasswordController(
    ISender sender,
    ICurrentUserService currentUserService,
    IWebHostEnvironment environment) : ApiControllerBase(sender)
{
    [HttpPost("change-password")]
    [ProducesResponseType(typeof(ApiResponse<AuthResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new ChangePasswordCommand(
                request.CurrentPassword ?? string.Empty,
                request.NewPassword ?? string.Empty,
                currentUserService.UserId.Value),
            cancellationToken);

        return result.IsSuccess
            ? Success(result.Value, StatusCodes.Status200OK, "Password updated successfully.")
            : HandleFailure(result);
    }

    [HttpPost("web/change-password")]
    [ProducesResponseType(typeof(ApiResponse<WebAuthResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> WebChangePassword(
        [FromBody] ChangePasswordRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!currentUserService.UserId.HasValue)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(
            new ChangePasswordCommand(
                request.CurrentPassword ?? string.Empty,
                request.NewPassword ?? string.Empty,
                currentUserService.UserId.Value),
            cancellationToken);

        if (result.IsFailure)
        {
            return HandleFailure(result);
        }

        // keepMeSignedIn: true — a password change replaces the session with a persistent one
        // regardless of how the prior session was established; UC-07 carries no such input.
        WebRefreshCookie.Append(
            HttpContext,
            environment,
            result.Value.RefreshToken,
            result.Value.RefreshTokenExpiresAtUtc,
            keepMeSignedIn: true);

        return Success(
            WebAuthResponseDto.From(result.Value),
            message: "Password updated successfully.");
    }
}

public sealed record ChangePasswordRequestDto(string? CurrentPassword, string? NewPassword);