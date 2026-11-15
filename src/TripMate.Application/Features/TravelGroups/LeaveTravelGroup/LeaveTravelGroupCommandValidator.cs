using FluentValidation;

namespace TripMate.Application.Features.TravelGroups.LeaveTravelGroup;

public sealed class LeaveTravelGroupCommandValidator : AbstractValidator<LeaveTravelGroupCommand>
{
    public LeaveTravelGroupCommandValidator()
    {
        RuleFor(command => command.GroupId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEqual(Guid.Empty);
    }
}