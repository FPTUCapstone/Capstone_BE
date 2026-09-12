using FluentValidation;
using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.GoogleAuth;

public class GoogleAuthCommandValidator : AbstractValidator<GoogleAuthCommand>
{
    public GoogleAuthCommandValidator()
    {
        RuleFor(x => x.IdToken)
            .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01);
    }
}
