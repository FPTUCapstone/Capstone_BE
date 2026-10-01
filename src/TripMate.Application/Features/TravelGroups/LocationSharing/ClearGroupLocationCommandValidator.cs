using FluentValidation;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class ClearGroupLocationCommandValidator : AbstractValidator<ClearGroupLocationCommand>
{
    public ClearGroupLocationCommandValidator()
    {
        RuleFor(command => command.GroupId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
    }
}