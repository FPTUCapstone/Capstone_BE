using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.Tours.GetCategories;

public sealed record TourCategoryDto(int CategoryId, string Code, string Name);

public sealed record GetTourCategoriesQuery : IRequest<Result<IReadOnlyList<TourCategoryDto>>>;

public sealed class GetTourCategoriesQueryHandler(IApplicationDbContext context)
    : IRequestHandler<GetTourCategoriesQuery, Result<IReadOnlyList<TourCategoryDto>>>
{
    public async Task<Result<IReadOnlyList<TourCategoryDto>>> Handle(
        GetTourCategoriesQuery request, CancellationToken cancellationToken)
    {
        var categories = await context.TourCategories.AsNoTracking()
            .Where(category => category.IsActive)
            .OrderBy(category => category.Name).ThenBy(category => category.Code)
            .Select(category => new TourCategoryDto(category.Id, category.Code, category.Name))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<TourCategoryDto>>(categories);
    }
}