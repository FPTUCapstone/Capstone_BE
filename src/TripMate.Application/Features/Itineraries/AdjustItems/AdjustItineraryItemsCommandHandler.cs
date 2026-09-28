using System.Security.Cryptography;
using System.Text;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Itineraries.Common;
using TripMate.Application.Features.Scheduling.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Itineraries.AdjustItems;

public sealed class AdjustItineraryItemsCommandHandler(
    IApplicationDbContext dbContext,
    IItineraryAccessService accessService,
    IItineraryVersionService versionService,
    IItineraryMutationLock itineraryMutationLock,
    IDateTimeProvider dateTimeProvider)
    : IRequestHandler<AdjustItineraryItemsCommand, Result<ItineraryDetailResponse>>
{
    public async Task<Result<ItineraryDetailResponse>> Handle(
        AdjustItineraryItemsCommand command,
        CancellationToken cancellationToken)
    {
        return await dbContext.ExecuteInSerializableTransactionAsync(
            async transactionCancellationToken =>
            {
                await itineraryMutationLock.AcquireAsync(
                    command.ItineraryId,
                    transactionCancellationToken);

                var accessResult = await accessService.ResolveCurrentAsync(
                    command.ItineraryId,
                    command.TravelerUserId,
                    trackCurrent: true,
                    transactionCancellationToken);
                if (accessResult.IsFailure)
                {
                    return Failure(accessResult);
                }

                var access = accessResult.Value;
                if (!access.CanManage)
                {
                    return Result.Failure<ItineraryDetailResponse>(
                        ItineraryErrorCodes.OwnerPermissionRequired,
                        "Only the itinerary owner can adjust it.");
                }

                var requestHash = ComputeHash(command.ItineraryId, command.OrderedVisitPoiIds);
                var operation = await dbContext.ItineraryVersionOperations
                    .SingleOrDefaultAsync(item =>
                        item.TravelerUserId == command.TravelerUserId
                        && item.IdempotencyKey == command.IdempotencyKey,
                        transactionCancellationToken);
                if (operation is not null)
                {
                    if (operation.OperationType != ItineraryVersionOperationType.Adjust
                        || !string.Equals(operation.RequestHash, requestHash, StringComparison.Ordinal))
                    {
                        return Result.Failure<ItineraryDetailResponse>(
                            ItineraryErrorCodes.IdempotencyKeyPayloadMismatch,
                            "The Idempotency-Key was already used with different itinerary data.");
                    }

                    return operation.ResultItineraryId.HasValue
                        ? await LoadResponseAsync(operation.ResultItineraryId.Value, transactionCancellationToken)
                        : Result.Failure<ItineraryDetailResponse>(
                            ItineraryErrorCodes.InvalidState,
                            "The previous itinerary operation has not completed.");
                }

                var visitIds = await dbContext.ItineraryItems
                    .Where(item => item.ItineraryId == access.CurrentItinerary.Id
                        && item.Kind == ItineraryItemKind.Visit
                        && item.PointOfInterestId.HasValue)
                    .Select(item => item.PointOfInterestId!.Value)
                    .ToListAsync(transactionCancellationToken);
                var visitIdSet = visitIds.ToHashSet();
                if (command.OrderedVisitPoiIds.Any(id => !visitIdSet.Contains(id)))
                {
                    return Result.Failure<ItineraryDetailResponse>(
                        ItineraryErrorCodes.InvalidState,
                        "The selected locations are not part of the current itinerary.");
                }

                if (!access.CurrentItinerary.SchedulingRequestId.HasValue)
                {
                    return Result.Failure<ItineraryDetailResponse>(
                        ItineraryErrorCodes.InvalidState,
                        "This itinerary cannot be adjusted.");
                }

                var schedulingRequest = await dbContext.SchedulingRequests
                    .SingleAsync(item => item.Id == access.CurrentItinerary.SchedulingRequestId.Value, transactionCancellationToken);
                var successorResult = await versionService.CreateAdjustedVersionAsync(
                    access.CurrentItinerary,
                    schedulingRequest,
                    command.OrderedVisitPoiIds.ToArray(),
                    transactionCancellationToken);
                if (successorResult.IsFailure)
                {
                    return Result.Failure<ItineraryDetailResponse>(
                        successorResult.ErrorCode!,
                        successorResult.ErrorMessage!);
                }

                var operationRecord = ItineraryVersionOperation.Create(
                    command.TravelerUserId,
                    access.CurrentItinerary.Id,
                    ItineraryVersionOperationType.Adjust,
                    command.IdempotencyKey,
                    requestHash,
                    dateTimeProvider.UtcNow);
                dbContext.ItineraryVersionOperations.Add(operationRecord);
                await dbContext.SaveChangesAsync(transactionCancellationToken);
                operationRecord.Complete(successorResult.Value.Id);
                await dbContext.SaveChangesAsync(transactionCancellationToken);

                return await LoadResponseAsync(successorResult.Value.Id, transactionCancellationToken);
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
        return Result.Success(GetDetail.GetItineraryDetailQueryHandler.ToResponse(
            itinerary,
            true,
            itinerary.SchedulingRequestId ?? 0));
    }

    private static Result<ItineraryDetailResponse> Failure(Result<ItineraryAccess> result) =>
        Result.Failure<ItineraryDetailResponse>(result.ErrorCode!, result.ErrorMessage!);

    private static string ComputeHash(long itineraryId, IReadOnlyCollection<long> orderedVisitPoiIds)
    {
        var payload = $"adjust|{itineraryId}|{string.Join(',', orderedVisitPoiIds)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}