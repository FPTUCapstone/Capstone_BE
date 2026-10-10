using FluentValidation;

namespace TripMate.Application.Features.Navigation.Complete;

public sealed class CompleteNavigationSessionCommandValidator
    : AbstractValidator<CompleteNavigationSessionCommand>
{
    public CompleteNavigationSessionCommandValidator()
    {
        RuleFor(command => command.SessionId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
    }
}