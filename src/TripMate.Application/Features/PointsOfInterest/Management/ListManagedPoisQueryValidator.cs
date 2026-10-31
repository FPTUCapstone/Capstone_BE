using FluentValidation;

namespace TripMate.Application.Features.PointsOfInterest.Management;

public sealed class ListManagedPoisQueryValidator : AbstractValidator<ListManagedPoisQuery>
{
    public ListManagedPoisQueryValidator()
    {
        RuleFor(query => query.Page).GreaterThanOrEqualTo(ListManagedPoisQuery.MinPage);
        RuleFor(query => query.PageSize)
            .InclusiveBetween(ListManagedPoisQuery.MinPageSize, ListManagedPoisQuery.MaxPageSize);
        RuleFor(query => query.Search)
            .Must(search => search is null || search.Trim().Length <= ListManagedPoisQuery.SearchMaxLength)
            .WithMessage($"Search must be {ListManagedPoisQuery.SearchMaxLength} characters or fewer after trimming.");
        RuleFor(query => query.CategoryId)
            .GreaterThanOrEqualTo(ListManagedPoisQuery.MinCategoryId)
            .When(query => query.CategoryId.HasValue);
        RuleFor(query => query.Status)
            .Must(status => status is null || ListManagedPoisQuery.AllowedStatuses.Contains(status))
            .WithMessage($"Status must be one of: {string.Join(", ", ListManagedPoisQuery.AllowedStatuses)}.");
        RuleFor(query => query.Page)
            .Must((query, page) => ((long)page - 1) * query.PageSize <= int.MaxValue)
            .WithMessage("The requested page offset is outside the supported range.")
            .When(query => query.Page >= ListManagedPoisQuery.MinPage
                && query.PageSize >= ListManagedPoisQuery.MinPageSize
                && query.PageSize <= ListManagedPoisQuery.MaxPageSize);
    }
}