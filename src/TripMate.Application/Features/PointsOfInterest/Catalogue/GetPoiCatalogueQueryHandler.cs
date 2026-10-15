using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;

namespace TripMate.Application.Features.PointsOfInterest.Catalogue;

public sealed class GetPoiCatalogueQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetPoiCatalogueQuery, Result<PoiCatalogueDto>>
{
    public async Task<Result<PoiCatalogueDto>> Handle(
        GetPoiCatalogueQuery request,
        CancellationToken cancellationToken)
    {
        var categories = await dbContext.PoiCategories
            .AsNoTracking()
            .OrderBy(category => category.Name)
            .ThenBy(category => category.Id)
            .Select(category => new PoiCatalogueOptionDto(category.Id, category.Name))
            .ToListAsync(cancellationToken);

        var tags = await dbContext.Tags
            .AsNoTracking()
            .OrderBy(tag => tag.Name)
            .ThenBy(tag => tag.Id)
            .Select(tag => new PoiCatalogueOptionDto(tag.Id, tag.Name))
            .ToListAsync(cancellationToken);

        return Result.Success(new PoiCatalogueDto(categories, tags));
    }
}