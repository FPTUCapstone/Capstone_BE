using FluentValidation;

namespace TripMate.Application.Features.Navigation.Start;

public sealed class StartNavigationSessionCommandValidator
    : AbstractValidator<StartNavigationSessionCommand>
{
    public StartNavigationSessionCommandValidator()
    {
        RuleFor(command => command.ItineraryId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEmpty();
    }
}