using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.GetDetail;

public sealed class GetItineraryDetailQueryHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService accessService)
    : IRequestHandler<GetItineraryDetailQuery, Result<ItineraryDetailResponse>>
{
    public async Task<Result<ItineraryDetailResponse>> Handle(
        GetItineraryDetailQuery request,
        CancellationToken cancellationToken)
    {
        var accessResult = await accessService.ResolveCurrentAsync(
            request.ItineraryId,
            request.TravelerUserId,
            trackCurrent: false,
            cancellationToken);
        if (accessResult.IsFailure)
        {
            return Result.Failure<ItineraryDetailResponse>(
                accessResult.ErrorCode!,
                accessResult.ErrorMessage!);
        }

        var access = accessResult.Value;
        var itinerary = await dbContext.Itineraries
            .AsNoTracking()
            .Include(item => item.Items)
            .ThenInclude(item => item.PointOfInterest)
            .ThenInclude(poi => poi!.Category)
            .SingleAsync(item => item.Id == access.CurrentItinerary.Id, cancellationToken);

        return Result.Success(ToResponse(itinerary, access.CanManage, access.SchedulingRequestId));
    }

    internal static ItineraryDetailResponse ToResponse(
        Domain.Entities.Itinerary itinerary,
        bool canManage,
        long schedulingRequestId)
    {
        var orderedItems = itinerary.Items.OrderBy(item => item.SequenceNo).ToArray();
        var items = orderedItems
            .Select((item, index) => new ItineraryDetailItemDto(
                item.Id,
                item.SequenceNo,
                item.PointOfInterestId,
                item.PointOfInterest?.Name,
                item.PointOfInterest?.Category?.Name,
                item.Kind,
                item.PlannedArrivalUtc,
                item.PlannedDepartureUtc,
                index == 0
                    ? null
                    : orderedItems[index - 1].TravelDurationToNextMinutes,
                item.StayDurationMinutes,
                item.EstimatedCost,
                item.IsMandatory,
                item.RecommendationReason,
                item.FriendlyExplanation,
                item.PointOfInterest is not null
                    && item.PointOfInterest.Status != PointOfInterestStatus.Active,
                item.PointOfInterest?.Latitude,
                item.PointOfInterest?.Longitude))
            .ToArray();

        return new ItineraryDetailResponse(
            itinerary.Id,
            schedulingRequestId,
            itinerary.Title,
            itinerary.Version,
            itinerary.Status,
            itinerary.ValidFromUtc,
            itinerary.ValidToUtc,
            canManage,
            orderedItems.Sum(item => item.EstimatedCost ?? 0m),
            itinerary.ValidFromUtc.HasValue && itinerary.ValidToUtc.HasValue
                ? (int)Math.Ceiling((itinerary.ValidToUtc.Value - itinerary.ValidFromUtc.Value).TotalMinutes)
                : orderedItems.Sum(item => item.StayDurationMinutes),
            items);
    }
}