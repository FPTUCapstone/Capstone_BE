using FluentValidation;

namespace TripMate.Application.Features.Authentication.Common;

/// <summary>
/// Shared credential rules so every authentication flow validates email and password with
/// the same policy (MSG01/MSG02/MSG05) — there is exactly one password policy per account.
/// </summary>
public static class AuthenticationRuleExtensions
{
    public static IRuleBuilderOptions<T, string> ApplyEmailRule<T>(this IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01).WithMessage("Email is required.")
            .EmailAddress().WithErrorCode(AuthErrorCodes.Msg02).WithMessage("Invalid email format.")
            .MaximumLength(254);

    public static IRuleBuilderOptions<T, string> ApplyPasswordPolicy<T>(this IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01).WithMessage("Password is required.")
            .MinimumLength(8).WithErrorCode(AuthErrorCodes.Msg05).WithMessage("Password must be at least 8 characters.")
            .MaximumLength(72)
            .Must(static password => !password.Any(char.IsWhiteSpace))
                .WithErrorCode(AuthErrorCodes.Msg05)
                .WithMessage("Password must not contain whitespace.")
            .Matches(@"[A-Z]").WithErrorCode(AuthErrorCodes.Msg05).WithMessage("Password must contain at least one uppercase letter.")
            .Matches(@"[a-z]").WithErrorCode(AuthErrorCodes.Msg05).WithMessage("Password must contain at least one lowercase letter.")
            .Matches(@"[0-9]").WithErrorCode(AuthErrorCodes.Msg05).WithMessage("Password must contain at least one number.")
            .Matches(@"[^a-zA-Z0-9]").WithErrorCode(AuthErrorCodes.Msg05).WithMessage("Password must contain at least one special character.");
}