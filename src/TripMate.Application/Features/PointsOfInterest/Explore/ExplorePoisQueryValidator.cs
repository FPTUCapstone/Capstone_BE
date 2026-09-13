using FluentValidation;

namespace TripMate.Application.Features.PointsOfInterest.Explore;

public sealed class ExplorePoisQueryValidator : AbstractValidator<ExplorePoisQuery>
{
    private static readonly string[] AllowedSortValues = ["name", "distance", "rating"];

    public ExplorePoisQueryValidator()
    {
        RuleFor(q => q.Search)
            .Must(search => search is null || search.Trim().Length <= ExplorePoisQuery.SearchMaxLength)
            .WithMessage(
                $"'{{PropertyName}}' must be {ExplorePoisQuery.SearchMaxLength} characters or fewer after trimming.");

        RuleFor(q => q.CategoryId)
            .GreaterThan(0)
            .When(q => q.CategoryId.HasValue);

        RuleFor(q => q.OriginLatitude)
            .InclusiveBetween(-90m, 90m)
            .When(q => q.OriginLatitude.HasValue);

        RuleFor(q => q.OriginLatitude)
            .NotNull()
            .WithMessage("'Origin Latitude' must be supplied together with 'Origin Longitude'.")
            .When(q => q.OriginLongitude.HasValue);

        RuleFor(q => q.OriginLongitude)
            .InclusiveBetween(-180m, 180m)
            .When(q => q.OriginLongitude.HasValue);

        RuleFor(q => q.OriginLongitude)
            .NotNull()
            .WithMessage("'Origin Longitude' must be supplied together with 'Origin Latitude'.")
            .When(q => q.OriginLatitude.HasValue);

        RuleFor(q => q.MaxDistanceKm)
            .GreaterThan(0m)
            .When(q => q.MaxDistanceKm.HasValue);

        RuleFor(q => q.MaxDistanceKm)
            .Null()
            .WithMessage("'Max Distance Km' requires both origin coordinates.")
            .When(q => !q.OriginLatitude.HasValue || !q.OriginLongitude.HasValue);

        RuleFor(q => q.Sort)
            .Must(sort => AllowedSortValues.Contains(sort))
            .WithMessage(
                $"'{{PropertyName}}' must be one of: {string.Join(", ", AllowedSortValues)}.");

        RuleFor(q => q.Sort)
            .Must((q, _) => q.OriginLatitude.HasValue && q.OriginLongitude.HasValue)
            .WithMessage("Sorting by distance requires both origin coordinates.")
            .When(q => q.Sort == "distance");

        RuleFor(q => q.Page).GreaterThan(0);

        RuleFor(q => q.PageSize)
            .InclusiveBetween(1, ExplorePoisQuery.MaxPageSize);
    }
}