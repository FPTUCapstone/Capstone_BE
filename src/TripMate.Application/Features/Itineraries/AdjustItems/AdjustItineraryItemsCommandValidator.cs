using FluentValidation;

namespace TripMate.Application.Features.Itineraries.AdjustItems;

public sealed class AdjustItineraryItemsCommandValidator : AbstractValidator<AdjustItineraryItemsCommand>
{
    public AdjustItineraryItemsCommandValidator()
    {
        RuleFor(command => command.ItineraryId).GreaterThan(0);
        RuleFor(command => command.TravelerUserId).GreaterThan(0);
        RuleFor(command => command.IdempotencyKey).NotEmpty();
        RuleFor(command => command.OrderedVisitPoiIds)
            .Cascade(CascadeMode.Stop)
            .NotNull()
            .NotEmpty()
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("Visit locations must be distinct.");
    }
}