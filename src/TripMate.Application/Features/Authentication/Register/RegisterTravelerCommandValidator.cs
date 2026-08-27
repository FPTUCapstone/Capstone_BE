using FluentValidation;

namespace TripMate.Application.Features.Authentication.Register;

public class RegisterTravelerCommandValidator : AbstractValidator<RegisterTravelerCommand>
{
    public RegisterTravelerCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(8)
            .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain at least one lowercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit.");

        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
    }
}
