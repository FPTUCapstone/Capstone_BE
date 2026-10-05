using FluentValidation;

namespace TripMate.Application.Features.Personalization.Recommendations;

public sealed class GetPoiRecommendationsQueryValidator
    : AbstractValidator<GetPoiRecommendationsQuery>
{
    public GetPoiRecommendationsQueryValidator()
    {
        RuleFor(query => query.ExplorationLatitude).InclusiveBetween(-90m, 90m);
        RuleFor(query => query.ExplorationLongitude).InclusiveBetween(-180m, 180m);
        RuleFor(query => query.SearchRadiusKm).InclusiveBetween(1, 50);
        RuleFor(query => query.Limit)
            .InclusiveBetween(1, 20)
            .When(query => query.Limit.HasValue);
    }
}
