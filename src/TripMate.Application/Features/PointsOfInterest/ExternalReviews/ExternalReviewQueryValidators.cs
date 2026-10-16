using FluentValidation;

namespace TripMate.Application.Features.PointsOfInterest.ExternalReviews;

public sealed class GetPoiExternalReviewsQueryValidator : AbstractValidator<GetPoiExternalReviewsQuery>
{
    public GetPoiExternalReviewsQueryValidator()
    {
        RuleFor(query => query.PoiId).GreaterThan(0);
        RuleFor(query => query.Size).InclusiveBetween(1, GetPoiExternalReviewsQuery.MaxSize);
    }
}

public sealed class TranslateExternalReviewQueryValidator : AbstractValidator<TranslateExternalReviewQuery>
{
    public TranslateExternalReviewQueryValidator()
    {
        RuleFor(query => query.PoiId).GreaterThan(0);
        RuleFor(query => query.ReviewId).GreaterThan(0);
        RuleFor(query => query.Target)
            .Must(target => TranslateExternalReviewQuery.SupportedTargets.Contains(target))
            .WithMessage("Only Vietnamese translation is supported.");
    }
}