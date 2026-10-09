using FluentValidation;

namespace TripMate.Application.Features.Navigation.Get;

public sealed class GetNavigationSessionQueryValidator : AbstractValidator<GetNavigationSessionQuery>
{
    public GetNavigationSessionQueryValidator()
    {
        RuleFor(query => query.SessionId).GreaterThan(0);
        RuleFor(query => query.TravelerUserId).GreaterThan(0);
    }
}