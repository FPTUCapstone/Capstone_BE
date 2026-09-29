using FluentValidation;

namespace TripMate.Application.Features.PointsOfInterest.Search;

public sealed class SearchSelectablePoisQueryValidator
    : AbstractValidator<SearchSelectablePoisQuery>
{
    public SearchSelectablePoisQueryValidator()
    {
        RuleFor(query => query)
            .Must(HasCompleteLocationOrNone)
            .WithMessage("Latitude, longitude, and radiusKm must be supplied together.");
        When(
            query => query.Latitude.HasValue
                && query.Longitude.HasValue
                && query.RadiusKm.HasValue,
            () =>
        {
            RuleFor(query => query.Latitude!.Value).InclusiveBetween(-90m, 90m);
            RuleFor(query => query.Longitude!.Value).InclusiveBetween(-180m, 180m);
            RuleFor(query => query.RadiusKm!.Value).InclusiveBetween(1, 50);
        });
        RuleFor(query => query.Page).GreaterThan(0);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 50);
        RuleFor(query => query.Query).MaximumLength(200);
    }

    private static bool HasCompleteLocationOrNone(SearchSelectablePoisQuery query) =>
        query.Latitude.HasValue == query.Longitude.HasValue
        && query.Latitude.HasValue == query.RadiusKm.HasValue;
}