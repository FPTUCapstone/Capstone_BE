using MediatR;

using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Authentication.PasswordReset;

/// <summary>Input contract for POST /api/v1/auth/password-reset/request.</summary>
public record RequestPasswordResetCommand(string Email)
    : IRequest<Result<RequestPasswordResetResponse>>;

/// <summary>
/// Generic, enumeration-safe response: every account-specific outcome (eligible, unknown,
/// Google-only, locked, inactive, cooldown, delivery failure/unknown) returns this exact
/// message. It never reveals existence, status, provider type, cooldown, delivery result,
/// generation, or OTP.
/// </summary>
public sealed record RequestPasswordResetResponse(string Message)
{
    public const string GenericMessage =
        "If an account exists for this email, reset instructions have been sent.";
}

/// <summary>
/// Input contract for POST /api/v1/auth/password-reset/confirm. Code stays a string so
/// leading zeros ("000001") remain valid representations — it is never parsed as a number.
/// </summary>
public record ConfirmPasswordResetCommand(string Email, string Code, string NewPassword)
    : IRequest<Result<ConfirmPasswordResetResponse>>;

/// <summary>Direct typed success DTO for the confirm endpoint (no legacy envelope).</summary>
public sealed record ConfirmPasswordResetResponse(string Message)
{
    public const string SuccessMessage =
        "Your password has been reset. You can now sign in with your new password.";
}