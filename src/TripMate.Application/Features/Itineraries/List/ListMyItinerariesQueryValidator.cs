using FluentValidation;

namespace TripMate.Application.Features.Itineraries.List;

public sealed class ListMyItinerariesQueryValidator : AbstractValidator<ListMyItinerariesQuery>
{
    public ListMyItinerariesQueryValidator()
    {
        RuleFor(query => query.TravelerUserId).GreaterThan(0);
        RuleFor(query => query.Page).GreaterThanOrEqualTo(1);
        RuleFor(query => query.PageSize).InclusiveBetween(1, ListMyItinerariesQuery.MaxPageSize);
        RuleFor(query => query.Sort)
            .Must(IsSupportedSort)
            .When(query => !string.IsNullOrWhiteSpace(query.Sort))
            .WithMessage($"Sort must be '{ListMyItinerariesQuery.PrioritySort}' or '{ListMyItinerariesQuery.NewestSort}'.");
    }

    private static bool IsSupportedSort(string? sort)
    {
        var trimmed = sort?.Trim();
        return string.Equals(trimmed, ListMyItinerariesQuery.PrioritySort, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, ListMyItinerariesQuery.NewestSort, StringComparison.OrdinalIgnoreCase);
    }
}