using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TripMate.Api.Common;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.GoogleAuth;
using TripMate.Application.Features.Authentication.Login;
using TripMate.Application.Features.Authentication.Register;
using TripMate.Application.Features.Authentication.SignOut;
using TripMate.Application.Features.Authentication.VerifyEmail;
using TripMate.Application.Features.Authentication.WebRefresh;
using TripMate.Application.Features.Authentication.WebSignIn;
using TripMate.Application.Features.Authentication.WebVerifyEmail;

namespace TripMate.Api.Controllers.V1;

public record RegisterTravelerRequestDto(
    string Email,
    string Password,
    string FullName,
    string? PhoneNumber,
    bool AcceptedTerms);

[AllowAnonymous]
[Route("api/v1/auth")]
public class AuthController(ISender sender, IWebHostEnvironment environment) : ApiControllerBase(sender)
{
    // Used only by the UC-01 flows (register / verify-email), whose contract requires the
    // Firebase ID token as `Authorization: Bearer`. The Google flow (UC-04) is body-only.
    private string? ExtractBearerToken()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return null;
        }

        const string prefix = "Bearer ";
        return authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authHeader[prefix.Length..].Trim()
            : authHeader.Trim();
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] RegisterTravelerRequestDto request,
        CancellationToken cancellationToken)
    {
        var token = ExtractBearerToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Error(
                StatusCodes.Status401Unauthorized,
                "Bearer Firebase ID token is required in the Authorization header.",
                new { code = "AUTH_HEADER_MISSING" });
        }

        var command = new RegisterTravelerCommand(
            request.Email,
            request.Password,
            request.FullName,
            request.PhoneNumber,
            request.AcceptedTerms,
            token);

        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess
            ? Success(
                result.Value,
                StatusCodes.Status201Created,
                "Account registered successfully! Please check your email for the verification code.")
            : HandleFailure(result);
    }

    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(CancellationToken cancellationToken)
    {
        var token = ExtractBearerToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Error(
                StatusCodes.Status401Unauthorized,
                "Bearer Firebase ID token is required in the Authorization header.",
                new { code = "AUTH_HEADER_MISSING" });
        }

        var command = new VerifyEmailCommand(token);
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? Success(
                result.Value,
                StatusCodes.Status200OK,
                "Email verified successfully.")
            : HandleFailure(result);
    }

    [HttpPost("web/login")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(ApiResponse<WebAuthResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> WebLogin(
        WebPasswordRequest request,
        CancellationToken cancellationToken) =>
        WebPasswordLogin(request, false, cancellationToken);

    [HttpPost("web/admin/login")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(ApiResponse<WebAuthResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> WebAdminLogin(
        WebPasswordRequest request,
        CancellationToken cancellationToken) =>
        WebPasswordLogin(request, true, cancellationToken);

    private async Task<IActionResult> WebPasswordLogin(
        WebPasswordRequest request,
        bool administratorOnly,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new WebPasswordSignInCommand(
                request.Email,
                request.Password,
                administratorOnly),
            cancellationToken);

        if (result.IsFailure)
        {
            return HandleFailure(result);
        }

        WebRefreshCookie.Append(
            HttpContext,
            environment,
            result.Value.RefreshToken,
            result.Value.RefreshTokenExpiresAtUtc,
            request.KeepMeSignedIn);

        return Success(
            WebAuthResponseDto.From(result.Value),
            message: "Sign in successful.");
    }

    [HttpPost("web/google")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(ApiResponse<WebGoogleAuthResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> WebGoogle(
        WebGoogleRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdToken))
        {
            return Problem(
                title: "Google ID token is required.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["errorCode"] = AuthErrorCodes.AuthTokenMissing
                });
        }

        var result = await Sender.Send(
            new GoogleAuthCommand(request.IdToken),
            cancellationToken);

        if (result.IsFailure)
        {
            return HandleFailure(result);
        }

        WebRefreshCookie.Append(
            HttpContext,
            environment,
            result.Value.RefreshToken,
            result.Value.RefreshTokenExpiresAtUtc,
            request.KeepMeSignedIn);

        return Success(
            WebGoogleAuthResponseDto.From(result.Value),
            message: "Google authentication successful.");
    }

    [HttpPost("web/refresh")]
    [ProducesResponseType(typeof(ApiResponse<WebAuthResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> WebRefresh(CancellationToken cancellationToken)
    {
        // S01 restoration: the HttpOnly cookie is the only credential; no body is read.
        // The refresh row itself is never rotated (fixed expiry), and no Set-Cookie is emitted.
        var token = Request.Cookies[WebRefreshCookie.Name];

        var result = await Sender.Send(
            new WebRefreshCommand(token),
            cancellationToken);

        if (result.IsFailure)
        {
            return HandleFailure(result);
        }

        return Success(
            WebAuthResponseDto.From(result.Value),
            message: "Session restored.");
    }

    [HttpPost("web/logout")]
    public async Task<IActionResult> WebLogout(
        CancellationToken cancellationToken)
    {
        var cookie = Request.Cookies[WebRefreshCookie.Name];
        IActionResult result;

        try
        {
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                await Sender.Send(
                    new SignOutCommand(cookie),
                    cancellationToken);
            }

            result = Ok(new SignOutResponseDto());
        }
        finally
        {
            WebRefreshCookie.Delete(HttpContext, environment);
        }

        return result;
    }

    [HttpPost("web/verify-email")]
    [ProducesResponseType(typeof(ApiResponse<WebVerifyEmailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> WebVerifyEmail(
        CancellationToken cancellationToken)
    {
        // Strict Web Bearer contract; do not alter the legacy Mobile header parser.
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";

        var token = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return Problem(
                title: "Bearer Firebase ID token is required in the Authorization header.",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?>
                {
                    ["errorCode"] = AuthErrorCodes.AuthHeaderMissing
                });
        }

        var result = await Sender.Send(
            new WebVerifyEmailCommand(token),
            cancellationToken);

        return result.IsSuccess
            ? Success(
                result.Value,
                StatusCodes.Status200OK,
                "Email verified successfully.")
            : HandleFailure(result);
    }

    [HttpPost("google")]
    [ProducesResponseType(typeof(ApiResponse<GoogleAuthResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GoogleAuth(
        [FromBody] GoogleAuthCommand? command,
        CancellationToken cancellationToken)
    {
        // Body-only (UC-04 spec §6.3): the Bearer header is not an input channel for the
        // Google flow — a missing token is a ProblemDetails 400 with a stable errorCode.
        var token = command?.IdToken;

        if (string.IsNullOrWhiteSpace(token))
        {
            return Problem(
                title: "Google ID token is required.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                {
                    ["errorCode"] = AuthErrorCodes.AuthTokenMissing,
                });
        }

        var cmd = new GoogleAuthCommand(token);
        var result = await Sender.Send(cmd, cancellationToken);

        return result.IsSuccess
            ? Success(
                result.Value,
                StatusCodes.Status200OK,
                "Google authentication successful.")
            : HandleFailure(result);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            LoginCommand.ForMobile(
                command.Email,
                command.Password),
            cancellationToken);

        return result.IsSuccess
            ? Success(
                result.Value,
                StatusCodes.Status200OK,
                "Sign in successful.")
            : HandleFailure(result);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        [FromBody] SignOutRequestDto request,
        CancellationToken cancellationToken)
    {
        await Sender.Send(
            new SignOutCommand(request.RefreshToken),
            cancellationToken);

        return Ok(new SignOutResponseDto());
    }
}

public sealed record WebPasswordRequest(
    string? Email,
    string? Password,
    bool KeepMeSignedIn = false);

public sealed record WebGoogleRequest(
    string? IdToken,
    bool KeepMeSignedIn = false);