using FluentValidation;

namespace TripMate.Application.Features.Navigation.Skip;

public sealed class SkipNavigationItemCommandValidator : AbstractValidator<SkipNavigationItemCommand>
{
    public SkipNavigationItemCommandValidator()
    {
        RuleFor(command => command.SessionId).GreaterThan(0);
        RuleFor(command => command.ItemId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
    }
}