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
        RuleFor(command => command.RemovedStops!)
            .Cascade(CascadeMode.Stop)
            .Must(removed => removed.Count <= AdjustItineraryItemsCommand.MaxStops)
            .WithMessage($"At most {AdjustItineraryItemsCommand.MaxStops} removed locations can have a reason.")
            .Must(removed => removed.All(stop => stop.PoiId > 0))
            .WithMessage("Location ids must be positive.")
            .Must(removed => removed.All(stop => Enum.IsDefined(stop.Reason)))
            .WithMessage("Choose a valid removal reason.")
            .Must(removed => removed.Select(stop => stop.PoiId).Distinct().Count() == removed.Count)
            .WithMessage("A removed location can have only one reason.")
            .When(command => command.RemovedStops is not null);
        RuleFor(command => command)
            .Must(command => !command.RemovedStops!.Any(removed => command.OrderedVisitPoiIds.Contains(removed.PoiId)))
            .WithMessage("A removed location cannot stay in the itinerary.")
            .When(command => command.RemovedStops is not null && command.OrderedVisitPoiIds is not null);
    }
}