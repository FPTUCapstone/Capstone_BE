using FluentValidation;

using TripMate.Domain.Entities;

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
            // Same bound as the planner, which narrows its POI pickers to this radius.
            RuleFor(query => query.RadiusKm!.Value)
                .InclusiveBetween(SchedulingRequest.MinSearchRadiusKm, SchedulingRequest.MaxSearchRadiusKm);
        });
        RuleFor(query => query.Page).GreaterThan(0);
        RuleFor(query => query.PageSize).InclusiveBetween(1, 50);
        RuleFor(query => query.Query).MaximumLength(200);
    }

    private static bool HasCompleteLocationOrNone(SearchSelectablePoisQuery query) =>
        query.Latitude.HasValue == query.Longitude.HasValue
        && query.Latitude.HasValue == query.RadiusKm.HasValue;
}