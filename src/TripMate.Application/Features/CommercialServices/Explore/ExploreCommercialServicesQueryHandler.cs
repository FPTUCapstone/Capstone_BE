using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.CommercialServices.Explore;

public sealed class ExploreCommercialServicesQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ExploreCommercialServicesQuery, Result<PagedCommercialServiceResponseDto>>
{
    public async Task<Result<PagedCommercialServiceResponseDto>> Handle(
        ExploreCommercialServicesQuery request,
        CancellationToken cancellationToken)
    {
        var services = dbContext.CommercialServices
            .AsNoTracking()
            .Where(service =>
                service.AvailabilityStatus == CommercialService.AvailabilityAvailable
                && service.Provider.Status == ServiceProvider.StatusActive);

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            services = services.Where(service => service.ServiceCategory == request.Category);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            services = services.Where(service =>
                service.Name.Contains(search)
                || service.Provider.Name.Contains(search));
        }

        var totalCount = await services.CountAsync(cancellationToken);
        var totalPages = totalCount == 0
            ? 0
            : (int)Math.Ceiling(totalCount / (double)request.PageSize);

        if (totalCount == 0 || request.Page > totalPages)
        {
            return Result.Success(new PagedCommercialServiceResponseDto(
                request.Page,
                request.PageSize,
                totalCount,
                totalPages,
                []));
        }

        var offset = checked((request.Page - 1) * request.PageSize);

        var records = await services
            .OrderBy(service => service.ServiceCategory)
            .ThenBy(service => service.Name)
            .ThenBy(service => service.Id)
            .Skip(offset)
            .Take(request.PageSize)
            .Select(service => new
            {
                service.Id,
                service.ServiceCategory,
                service.Name,
                ProviderName = service.Provider.Name,
                service.Description,
                service.PriceAmount,
                service.CurrencyCode,
                service.PriceUnit,
                service.PriceIncludesTax,
                service.RefundableDepositAmount,
                service.Capacity,
                service.AttributesJson,
                service.CoverImageUrl,
                service.PoiId,
                service.FulfilmentLocationLabel,
                service.LastUpdatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        var items = records.Select(service => new CommercialServiceListItemDto(
            service.Id,
            service.ServiceCategory,
            service.Name,
            service.ProviderName,
            service.Description,
            service.PriceAmount,
            service.CurrencyCode,
            service.PriceUnit,
            service.PriceIncludesTax,
            service.RefundableDepositAmount,
            service.Capacity,
            CommercialServiceAttributeSanitizer.Sanitize(
                service.ServiceCategory,
                service.AttributesJson),
            service.CoverImageUrl,
            service.PoiId,
            service.FulfilmentLocationLabel,
            service.LastUpdatedAtUtc))
            .ToList();

        return Result.Success(new PagedCommercialServiceResponseDto(
            request.Page,
            request.PageSize,
            totalCount,
            totalPages,
            items));
    }
}