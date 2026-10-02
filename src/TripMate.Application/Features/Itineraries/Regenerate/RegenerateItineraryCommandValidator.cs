using FluentValidation;

namespace TripMate.Application.Features.Itineraries.Regenerate;

public sealed class RegenerateItineraryCommandValidator : AbstractValidator<RegenerateItineraryCommand>
{
    public RegenerateItineraryCommandValidator()
    {
        RuleFor(command => command.ItineraryId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEmpty();
    }
}