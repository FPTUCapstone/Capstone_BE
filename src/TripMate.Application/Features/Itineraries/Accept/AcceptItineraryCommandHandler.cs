using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;

namespace TripMate.Application.Features.Itineraries.Accept;

public sealed class AcceptItineraryCommandHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService accessService,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<AcceptItineraryCommand, Result<ItineraryDetailResponse>>
{
    public async Task<Result<ItineraryDetailResponse>> Handle(
        AcceptItineraryCommand request,
        CancellationToken cancellationToken)
    {
        return await dbContext.ExecuteInSerializableTransactionAsync(
            async transactionCancellationToken =>
            {
                var accessResult = await accessService.ResolveCurrentAsync(
                    request.ItineraryId,
                    request.TravelerUserId,
                    trackCurrent: true,
                    transactionCancellationToken);
                if (accessResult.IsFailure)
                {
                    return Result.Failure<ItineraryDetailResponse>(
                        accessResult.ErrorCode!,
                        accessResult.ErrorMessage!);
                }

                var access = accessResult.Value;
                if (!access.CanManage)
                {
                    return Result.Failure<ItineraryDetailResponse>(
                        ItineraryErrorCodes.OwnerPermissionRequired,
                        "Only the itinerary owner can accept it.");
                }

                try
                {
                    access.CurrentItinerary.Accept(dateTimeProvider.UtcNow);
                }
                catch (InvalidOperationException exception)
                {
                    return Result.Failure<ItineraryDetailResponse>(
                        ItineraryErrorCodes.InvalidState,
                        exception.Message);
                }

                await dbContext.SaveChangesAsync(transactionCancellationToken);
                return await LoadResponseAsync(
                    access.CurrentItinerary.Id,
                    transactionCancellationToken);
            },
            cancellationToken);
    }

    private async Task<Result<ItineraryDetailResponse>> LoadResponseAsync(
        long itineraryId,
        CancellationToken cancellationToken)
    {
        var itinerary = await dbContext.Itineraries
            .AsNoTracking()
            .Include(item => item.Items)
            .ThenInclude(item => item.PointOfInterest)
            .ThenInclude(poi => poi!.Category)
            .SingleAsync(item => item.Id == itineraryId, cancellationToken);

        var schedulingRequestId = itinerary.SchedulingRequestId ?? 0;
        return Result.Success(
            GetDetail.GetItineraryDetailQueryHandler.ToResponse(itinerary, true, schedulingRequestId));
    }
}