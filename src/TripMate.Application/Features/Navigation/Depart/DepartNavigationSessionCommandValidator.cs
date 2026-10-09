using FluentValidation;

using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Navigation.Depart;

public sealed class DepartNavigationSessionCommandValidator : AbstractValidator<DepartNavigationSessionCommand>
{
    public DepartNavigationSessionCommandValidator()
    {
        RuleFor(command => command.SessionId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.State)
            .Equal(TripSession.NavigatingState, StringComparer.Ordinal)
            .WithMessage($"state must be '{TripSession.NavigatingState}'.");
    }
}