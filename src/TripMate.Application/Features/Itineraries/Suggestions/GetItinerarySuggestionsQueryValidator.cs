using FluentValidation;

namespace TripMate.Application.Features.Itineraries.Suggestions;

public sealed class GetItinerarySuggestionsQueryValidator : AbstractValidator<GetItinerarySuggestionsQuery>
{
    public GetItinerarySuggestionsQueryValidator()
    {
        RuleFor(query => query.ItineraryId).GreaterThan(0);
        RuleFor(query => query.TravelerUserId).GreaterThan(0);
        RuleFor(query => query.Kind)
            .Must(kind => GetItinerarySuggestionsQuery.AllowedKinds.Contains(kind))
            .WithMessage("Kind must be Food or Replace.");
        RuleFor(query => query.AfterItemId)
            .NotNull()
            .GreaterThan(0)
            .When(query => query.Kind == GetItinerarySuggestionsQuery.KindFood);
        RuleFor(query => query.ReplaceItemId)
            .NotNull()
            .GreaterThan(0)
            .When(query => query.Kind == GetItinerarySuggestionsQuery.KindReplace);
    }
}