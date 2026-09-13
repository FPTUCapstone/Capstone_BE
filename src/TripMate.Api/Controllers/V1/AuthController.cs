using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripMate.Api.Common;
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
    public async Task<IActionResult> GoogleAuth(
        [FromBody] GoogleAuthCommand? command,
        CancellationToken cancellationToken)
    {
        var token = command?.IdToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = ExtractBearerToken();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "Firebase ID token or Google token is required.",
                new { code = "AUTH_TOKEN_MISSING" });
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
