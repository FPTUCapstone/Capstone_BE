using FluentValidation;

namespace TripMate.Application.Features.RecommendationFeedback.Favorites;

public sealed class ListFavoritePoisQueryValidator : AbstractValidator<ListFavoritePoisQuery>
{
    public ListFavoritePoisQueryValidator()
    {
        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(ListFavoritePoisQuery.MinPage);

        RuleFor(q => q.PageSize)
            .InclusiveBetween(ListFavoritePoisQuery.MinPageSize, ListFavoritePoisQuery.MaxPageSize);
    }
}