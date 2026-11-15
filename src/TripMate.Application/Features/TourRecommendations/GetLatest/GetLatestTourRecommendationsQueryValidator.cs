using FluentValidation;

namespace TripMate.Application.Features.TourRecommendations.GetLatest;

public sealed class GetLatestTourRecommendationsQueryValidator
    : AbstractValidator<GetLatestTourRecommendationsQuery>
{
    public GetLatestTourRecommendationsQueryValidator()
    {
        RuleFor(query => query.Page).GreaterThan(0);
    }
}