using FluentValidation;

namespace TripMate.Application.Features.Itineraries.Accept;

public sealed class AcceptItineraryCommandValidator : AbstractValidator<AcceptItineraryCommand>
{
    public AcceptItineraryCommandValidator()
    {
        RuleFor(command => command.ItineraryId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
    }
}