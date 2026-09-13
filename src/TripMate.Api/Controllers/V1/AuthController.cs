using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripMate.Api.Common;
using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.GoogleAuth;
using TripMate.Application.Features.Authentication.Login;
using TripMate.Application.Features.Authentication.Register;
using TripMate.Application.Features.Authentication.VerifyEmail;

namespace TripMate.Api.Controllers.V1;

public record RegisterTravelerRequestDto(
    string Email,
    string Password,
    string FullName,
    string? PhoneNumber,
    bool AcceptedTerms);

[AllowAnonymous]
[Route("api/v1/auth")]
public class AuthController(ISender sender) : ApiControllerBase(sender)
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
            ? Success(result.Value, StatusCodes.Status201Created, "Account registered successfully! Please check your email for the verification code.")
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
            ? Success(result.Value, StatusCodes.Status200OK, "Email verified successfully.")
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
            ? Success(result.Value, StatusCodes.Status200OK, "Google authentication successful.")
            : HandleFailure(result);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginCommand command, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess
            ? Success(result.Value, StatusCodes.Status200OK, "Sign in successful.")
            : HandleFailure(result);
    }
}
