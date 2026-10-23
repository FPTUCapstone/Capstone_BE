using FluentValidation;

namespace TripMate.Application.Features.CommercialServices.Explore;

public sealed class ExploreCommercialServicesQueryValidator
    : AbstractValidator<ExploreCommercialServicesQuery>
{
    public ExploreCommercialServicesQueryValidator()
    {
        RuleFor(query => query.Category)
            .Must(category => category is null
                || ExploreCommercialServicesQuery.AllowedCategories.Contains(category))
            .WithMessage(
                $"'{{PropertyName}}' must be one of: {string.Join(", ", ExploreCommercialServicesQuery.AllowedCategories)}.");

        RuleFor(query => query.Search)
            .Must(search => search is null
                || search.Trim().Length <= ExploreCommercialServicesQuery.SearchMaxLength)
            .WithMessage(
                $"'{{PropertyName}}' must be {ExploreCommercialServicesQuery.SearchMaxLength} characters or fewer after trimming.");

        RuleFor(query => query.Page)
            .GreaterThanOrEqualTo(ExploreCommercialServicesQuery.MinPage);

        RuleFor(query => query.PageSize)
            .InclusiveBetween(
                ExploreCommercialServicesQuery.MinPageSize,
                ExploreCommercialServicesQuery.MaxPageSize);
    }
}