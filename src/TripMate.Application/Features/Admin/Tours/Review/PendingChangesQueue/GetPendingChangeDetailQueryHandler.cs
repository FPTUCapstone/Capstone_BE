using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Get;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Admin.Tours.Review.PendingChangesQueue;

public sealed class GetPendingChangeDetailQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetPendingChangeDetailQuery, Result<GetCurrentPendingChangeResponse>>
{
    public async Task<Result<GetCurrentPendingChangeResponse>> Handle(
        GetPendingChangeDetailQuery request, CancellationToken cancellationToken)
    {
        if (!await TourReviewReadAccess.IsActiveStaffAsync(dbContext, currentUser, cancellationToken))
        {
            return Result.Failure<GetCurrentPendingChangeResponse>(
                OperatorTourErrorCodes.AdminAccessRequired, "Active Staff access is required.");
        }

        TourPendingChangeSet? changeSet = await dbContext.TourPendingChangeSets.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == request.PendingChangeId, cancellationToken);
        if (changeSet is null)
        {
            return Result.Failure<GetCurrentPendingChangeResponse>(
                OperatorTourErrorCodes.PendingChangeNotFound, "The draft version is unavailable.");
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