using FluentValidation;

namespace TripMate.Application.Features.Admin.TourOperatorApplications.Approve;

public class ApproveOperatorApplicationCommandValidator : AbstractValidator<ApproveOperatorApplicationCommand>
{
    public ApproveOperatorApplicationCommandValidator()
    {
        RuleFor(x => x.UserId).GreaterThan(0).WithMessage("UserId must be greater than 0.");
    }
}
