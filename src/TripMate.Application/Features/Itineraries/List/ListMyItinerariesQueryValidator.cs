using FluentValidation;

namespace TripMate.Application.Features.Itineraries.List;

public sealed class ListMyItinerariesQueryValidator : AbstractValidator<ListMyItinerariesQuery>
{
    public ListMyItinerariesQueryValidator()
    {
        RuleFor(query => query.TravelerUserId).GreaterThan(0);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListMyItinerariesQuery.MaxPageSize);
    }
}