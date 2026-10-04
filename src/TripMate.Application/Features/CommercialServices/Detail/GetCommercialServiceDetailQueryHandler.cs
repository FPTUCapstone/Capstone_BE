using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.CommercialServices.Common;
using TripMate.Application.Features.CommercialServices.Explore;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.CommercialServices.Detail;

public sealed class GetCommercialServiceDetailQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetCommercialServiceDetailQuery, Result<CommercialServiceDetailDto>>
{
    public async Task<Result<CommercialServiceDetailDto>> Handle(
        GetCommercialServiceDetailQuery request,
        CancellationToken cancellationToken)
    {
        var service = await dbContext.CommercialServices
            .AsNoTracking()
            .Where(candidate =>
                candidate.Id == request.Id
                && candidate.AvailabilityStatus == CommercialService.AvailabilityAvailable
                && candidate.Provider.Status == ServiceProvider.StatusActive)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.ProviderId,
                candidate.ServiceCategory,
                candidate.Name,
                ProviderName = candidate.Provider.Name,
                candidate.Provider.ContactEmail,
                candidate.Provider.ContactPhone,
                candidate.Description,
                candidate.PriceAmount,
                candidate.CurrencyCode,
                candidate.PriceUnit,
                candidate.PriceIncludesTax,
                candidate.RefundableDepositAmount,
                candidate.Capacity,
                candidate.AttributesJson,
                candidate.CoverImageUrl,
                candidate.PoiId,
                candidate.FulfilmentLocationLabel,
                candidate.PickupOrArrivalInstructions,
                candidate.CancellationPolicySummary,
                candidate.LastUpdatedAtUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (service is null)
        {
            return Result.Failure<CommercialServiceDetailDto>(
                CommercialServiceErrorCodes.NotFound,
                CommercialServiceErrorMessages.NotFound);
        }

        return Result.Success(new CommercialServiceDetailDto(
            service.Id,
            service.ProviderId,
            service.ServiceCategory,
            service.Name,
            service.ProviderName,
            service.ContactEmail,
            service.ContactPhone,
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
            service.PickupOrArrivalInstructions,
            service.CancellationPolicySummary,
            service.LastUpdatedAtUtc));
    }
}