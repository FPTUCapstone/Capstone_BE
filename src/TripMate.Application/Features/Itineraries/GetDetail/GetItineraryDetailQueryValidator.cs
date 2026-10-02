using FluentValidation;

namespace TripMate.Application.Features.Itineraries.GetDetail;

public sealed class GetItineraryDetailQueryValidator : AbstractValidator<GetItineraryDetailQuery>
{
    public GetItineraryDetailQueryValidator()
    {
        RuleFor(query => query.ItineraryId).GreaterThan(0);
        RuleFor(query => query.TravelerUserId).GreaterThan(0);
    }
}