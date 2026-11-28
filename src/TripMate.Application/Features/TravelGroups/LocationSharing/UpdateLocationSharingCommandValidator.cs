using FluentValidation;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class UpdateLocationSharingCommandValidator : AbstractValidator<UpdateLocationSharingCommand>
{
    public UpdateLocationSharingCommandValidator()
    {
        RuleFor(command => command.GroupId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
    }
}