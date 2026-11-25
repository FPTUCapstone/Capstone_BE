using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Get;

public sealed class GetCurrentPendingChangeQueryHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser)
    : IRequestHandler<GetCurrentPendingChangeQuery, Result<GetCurrentPendingChangeResponse>>
{
    public async Task<Result<GetCurrentPendingChangeResponse>> Handle(
        GetCurrentPendingChangeQuery request, CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue)
        {
            return Result.Failure<GetCurrentPendingChangeResponse>(OperatorTourErrorCodes.AccessRequired,
                "An active approved Tour Operator account is required.");
        }
        long operatorId = currentUser.UserId.Value;

        bool ownsTour = await dbContext.Tours.AsNoTracking()
            .AnyAsync(tour => tour.Id == request.TourId && tour.OperatorUserId == operatorId, cancellationToken);
        if (!ownsTour)
        {
            return Result.Failure<GetCurrentPendingChangeResponse>(OperatorTourErrorCodes.TourNotFound,
                "The tour is unavailable.");
        }

        TourPendingChangeSet? changeSet = await dbContext.TourPendingChangeSets.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TourId == request.TourId &&
                (candidate.Status == TourPendingChangeStatus.Draft
                    || candidate.Status == TourPendingChangeStatus.PendingReview
                    || candidate.Status == TourPendingChangeStatus.Rejected),
                cancellationToken);
        if (changeSet is null)
        {
            return Result.Failure<GetCurrentPendingChangeResponse>(OperatorTourErrorCodes.PendingChangeNotFound,
                "No open draft version exists for this tour.");
        }

        return Result.Success(new GetCurrentPendingChangeResponse(
            changeSet.Id, changeSet.TourId, changeSet.Status.ToString(),
            changeSet.CategoryId == 0 ? null : changeSet.CategoryId,
            changeSet.Title, changeSet.Description, changeSet.BasePrice, changeSet.DurationDays,
            changeSet.Inclusions, changeSet.Exclusions, changeSet.Highlights,
            changeSet.TravellerRequirements, changeSet.TimeZoneId,
            PendingChangePayload.DeserializeDestinationIds(changeSet.DestinationIdsJson),
            changeSet.DestinationSummary, changeSet.CancellationPolicyTemplateId,
            PendingChangePayload.DeserializeItineraryItems(changeSet.ItineraryItemsJson),
            PendingChangePayload.DeserializeSchedules(changeSet.SchedulesJson),
            changeSet.CreatedAtUtc, changeSet.UpdatedAtUtc, changeSet.SubmittedAtUtc,
            changeSet.ReviewedAtUtc, changeSet.RejectionReason));
    }
}