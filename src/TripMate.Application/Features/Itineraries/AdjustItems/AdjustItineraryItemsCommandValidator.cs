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
            .Must(ids => ids.Count <= AdjustItineraryItemsCommand.MaxStops)
            .WithMessage($"An itinerary can have at most {AdjustItineraryItemsCommand.MaxStops} locations.")
            .Must(ids => ids.All(id => id > 0))
            .WithMessage("Location ids must be positive.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("Visit locations must be distinct.");
        RuleForEach(command => command.Stops)
            .Must(stop => stop.TransportModeToNext is null
                || AdjustItineraryItemsCommand.AllowedLegModes.Contains(stop.TransportModeToNext.Value))
            .WithMessage("Choose walking, motorbike, or car for each leg.")
            .When(command => command.Stops is not null);
        RuleFor(command => command)
            .Must(command => command.Stops is null
                || command.Stops.Select(stop => stop.PoiId).SequenceEqual(command.OrderedVisitPoiIds))
            .WithMessage("Stops and visit locations must describe the same order.")
            .When(command => command.Stops is not null);
    }
}