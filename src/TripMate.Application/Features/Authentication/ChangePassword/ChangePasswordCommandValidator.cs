using FluentValidation;

using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.ChangePassword;

/// <summary>
/// The "new password differs from current password" rule is NOT validated here: it requires
/// comparing against the stored hash (ChangePasswordCommandHandler), not a shallow string
/// comparison of two submitted values (UC-07 spec §3.5).
/// </summary>
public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword)
            .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01).WithMessage("Current password is required.");

        RuleFor(x => x.NewPassword).ApplyPasswordPolicy();
    }
}