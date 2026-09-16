using FluentValidation;

namespace TripMate.Application.Features.PointsOfInterest.Explore;

public sealed class ExplorePoisQueryValidator : AbstractValidator<ExplorePoisQuery>
{
    public ExplorePoisQueryValidator()
    {
        RuleFor(q => q.Search)
            .Must(search => search is null || search.Trim().Length <= ExplorePoisQuery.SearchMaxLength)
            .WithMessage(
                $"'{{PropertyName}}' must be {ExplorePoisQuery.SearchMaxLength} characters or fewer after trimming.");

        RuleFor(q => q.CategoryId)
            .GreaterThanOrEqualTo(ExplorePoisQuery.MinCategoryId)
            .When(q => q.CategoryId.HasValue);

        RuleFor(q => q.OriginLatitude)
            .InclusiveBetween(ExplorePoisQuery.MinLatitude, ExplorePoisQuery.MaxLatitude)
            .When(q => q.OriginLatitude.HasValue);

        RuleFor(q => q.OriginLatitude)
            .NotNull()
            .WithMessage("'Origin Latitude' must be supplied together with 'Origin Longitude'.")
            .When(q => q.OriginLongitude.HasValue);

        RuleFor(q => q.OriginLongitude)
            .InclusiveBetween(ExplorePoisQuery.MinLongitude, ExplorePoisQuery.MaxLongitude)
            .When(q => q.OriginLongitude.HasValue);

        RuleFor(q => q.OriginLongitude)
            .NotNull()
            .WithMessage("'Origin Longitude' must be supplied together with 'Origin Latitude'.")
            .When(q => q.OriginLatitude.HasValue);

        RuleFor(q => q.MaxDistanceKm)
            .GreaterThan(ExplorePoisQuery.MinDistanceKm)
            .When(q => q.MaxDistanceKm.HasValue);

        RuleFor(q => q.MaxDistanceKm)
            .Null()
            .WithMessage("'Max Distance Km' requires both origin coordinates.")
            .When(q => !q.OriginLatitude.HasValue || !q.OriginLongitude.HasValue);

        RuleFor(q => q.Sort)
            .Must(sort => ExplorePoisQuery.AllowedSorts.Contains(sort))
            .WithMessage(
                $"'{{PropertyName}}' must be one of: {string.Join(", ", ExplorePoisQuery.AllowedSorts)}.");

        RuleFor(q => q.Sort)
            .Must((q, _) => q.OriginLatitude.HasValue && q.OriginLongitude.HasValue)
            .WithMessage("Sorting by distance requires both origin coordinates.")
            .When(q => q.Sort == ExplorePoisQuery.SortDistance);

        RuleFor(q => q.Page).GreaterThanOrEqualTo(ExplorePoisQuery.MinPage);

        RuleFor(q => q.PageSize)
            .InclusiveBetween(ExplorePoisQuery.MinPageSize, ExplorePoisQuery.MaxPageSize);
    }
}