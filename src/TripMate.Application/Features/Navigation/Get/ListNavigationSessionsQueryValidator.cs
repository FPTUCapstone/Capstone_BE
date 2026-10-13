using FluentValidation;

namespace TripMate.Application.Features.Navigation.Get;

public sealed class ListNavigationSessionsQueryValidator : AbstractValidator<ListNavigationSessionsQuery>
{
    public ListNavigationSessionsQueryValidator()
    {
        RuleFor(query => query.TravelerUserId).GreaterThan(0);
    }
}