using FluentValidation;

namespace TripMate.Application.Features.TravelGroups.LocationSharing;

public sealed class PublishGroupLocationCommandValidator : AbstractValidator<PublishGroupLocationCommand>
{
    public PublishGroupLocationCommandValidator()
    {
        RuleFor(command => command.GroupId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(command => command.Longitude).InclusiveBetween(-180m, 180m);
        RuleFor(command => command.SessionVersion).NotEmpty();
    }
}