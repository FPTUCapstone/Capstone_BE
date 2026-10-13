using FluentValidation;

namespace TripMate.Application.Features.Navigation.Reach;

public sealed class ReachNavigationItemCommandValidator : AbstractValidator<ReachNavigationItemCommand>
{
    public ReachNavigationItemCommandValidator()
    {
        RuleFor(command => command.SessionId).GreaterThan(0);
        RuleFor(command => command.ItemId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
    }
}