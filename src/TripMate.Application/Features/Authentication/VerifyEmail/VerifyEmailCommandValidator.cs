using FluentValidation;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.VerifyEmail;

public class VerifyEmailCommandValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailCommandValidator()
    {
        RuleFor(x => x.FirebaseIdToken)
            .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01).WithMessage("Firebase ID token is required.");
    }
}
