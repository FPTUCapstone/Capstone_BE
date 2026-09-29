using FluentValidation;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.PasswordReset;

namespace TripMate.Application.Features.Authentication.PasswordReset;

public class RequestPasswordResetCommandValidator : AbstractValidator<RequestPasswordResetCommand>
{
    public RequestPasswordResetCommandValidator()
    {
        RuleFor(x => x.Email).ApplyEmailRule();
    }
}

public class ConfirmPasswordResetCommandValidator : AbstractValidator<ConfirmPasswordResetCommand>
{
    public ConfirmPasswordResetCommandValidator()
    {
        RuleFor(x => x.Email).ApplyEmailRule();

        RuleFor(x => x.Code)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01).WithMessage("Code is required.")
            .Length(6).WithErrorCode(AuthErrorCodes.Msg14).WithMessage("Invalid verification code.")
            .Matches(@"^[0-9]{6}$").WithErrorCode(AuthErrorCodes.Msg14).WithMessage("Invalid verification code.");

        RuleFor(x => x.NewPassword).ApplyPasswordPolicy();
    }
}