using FluentValidation;

using TripMate.Application.Features.Authentication.Common;

namespace TripMate.Application.Features.Authentication.Register;

public class RegisterTravelerCommandValidator : AbstractValidator<RegisterTravelerCommand>
{
    public RegisterTravelerCommandValidator()
    {
        RuleFor(x => x.Email).ApplyEmailRule();

        RuleFor(x => x.Password).ApplyPasswordPolicy();

        RuleFor(x => x.FullName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(AuthErrorCodes.Msg01).WithMessage("FullName is required.")
            .Must(x => !string.IsNullOrWhiteSpace(x)).WithErrorCode(AuthErrorCodes.Msg01).WithMessage("FullName is required.")
            .Must(x => x.Trim().Length >= 2).WithMessage("FullName must be at least 2 characters.")
            .Must(x => x.Trim().Length <= 150).WithMessage("FullName must not exceed 150 characters.")
            .Matches(@"^[\p{L}\p{Zs}]+$").WithMessage("FullName can only contain letters and spaces.");

        When(x => !string.IsNullOrWhiteSpace(x.PhoneNumber), () =>
        {
            RuleFor(x => x.PhoneNumber)
                .Matches(@"^0\d{9}$").WithErrorCode(AuthErrorCodes.Msg04).WithMessage("Invalid phone number. Phone number must be 10 digits starting with 0.");
        });

        RuleFor(x => x.AcceptedTerms)
            .Equal(true).WithErrorCode(AuthErrorCodes.MsgTos).WithMessage("You must accept the Terms of Service and Privacy Policy to continue.");
    }
}