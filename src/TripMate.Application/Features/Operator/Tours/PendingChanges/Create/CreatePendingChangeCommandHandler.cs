using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.Create;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Create;

/// <summary>
/// Creates (or resumes) a draft version of an Approved/Published tour (spec TM-82 §5.1). Idempotent:
/// an existing open (Draft/PendingReview/Rejected) change set is returned instead of duplicated.
/// </summary>
public sealed class CreatePendingChangeCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<CreatePendingChangeCommand, Result<PendingChangeResponse>>
{
    public async Task<Result<PendingChangeResponse>> Handle(
        CreatePendingChangeCommand request, CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return AccessDenied();
        long operatorId = currentUser.UserId.Value;

        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
                profile.UserId == operatorId && profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
                profile.User.Role == UserRole.TourOperator && profile.User.Status == AccountStatus.Active,
                transactionCancellationToken);
            if (!eligible) return AccessDenied();

            Tour? tour = await dbContext.Tours
                .Include(candidate => candidate.Destinations)
                .Include(candidate => candidate.ItineraryItems)
                .Include(candidate => candidate.Schedules)
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == request.TourId && candidate.OperatorUserId == operatorId,
                    transactionCancellationToken);
            if (tour is null)
                return Result.Failure<PendingChangeResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");

            if (tour.Status is TourStatus.Draft or TourStatus.Rejected)
            {
                return Result.Failure<PendingChangeResponse>(OperatorTourErrorCodes.InvalidState,
                    "Draft or Rejected tours are edited directly through the existing update endpoint.");
            }
            if (tour.Status == TourStatus.Pending)
            {
                return Result.Failure<PendingChangeResponse>(OperatorTourErrorCodes.InvalidState,
                    "MSG122: the tour package is currently pending approval.");
            }
            if (tour.Status == TourStatus.Inactive)
            {
                return Result.Failure<PendingChangeResponse>(OperatorTourErrorCodes.InvalidState,
                    "Inactive tours cannot have a draft version created.");
            }

            TourPendingChangeSet? existing = await dbContext.TourPendingChangeSets
                .SingleOrDefaultAsync(changeSet => changeSet.TourId == tour.Id &&
                    (changeSet.Status == TourPendingChangeStatus.Draft
                        || changeSet.Status == TourPendingChangeStatus.PendingReview
                        || changeSet.Status == TourPendingChangeStatus.Rejected),
                    transactionCancellationToken);
            if (existing is not null)
            {
                return Result.Success(new PendingChangeResponse(
                    existing.Id, existing.TourId, existing.Status.ToString(), IsNew: false));
            }

            DateTimeOffset now = clock.UtcNow;
            string destinationIdsJson = PendingChangePayload.SerializeDestinationIds(
                tour.Destinations.OrderBy(link => link.SequenceNo).Select(link => link.DestinationId));
            string itineraryItemsJson = PendingChangePayload.SerializeItineraryItems(
                tour.ItineraryItems.Select(item => new CreateTourItineraryItemRequest(
                    item.DayNo, item.SequenceNo, item.PoiId, item.PlannedTime, item.StayDurationMinutes,
                    item.Notes, item.IsRequiredForWeather)));
            string schedulesJson = PendingChangePayload.SerializeSchedules(
                tour.Schedules.Select(schedule => new PendingChangeScheduleItem(
                    schedule.Id, schedule.StartAtUtc, schedule.EndAtUtc, schedule.MeetingPoint,
                    schedule.TotalCapacity, schedule.MinimumCapacity, schedule.RegistrationDeadlineAtUtc,
                    schedule.AdultPrice, schedule.ChildPrice, schedule.InfantPrice,
                    schedule.MeetingLatitude, schedule.MeetingLongitude)));

            var changeSet = TourPendingChangeSet.CreateDraft(
                tour.Id, operatorId, tour.CategoryId ?? 0, tour.Title, tour.Description ?? string.Empty,
                tour.BasePrice, tour.DurationDays, tour.Inclusions, tour.Exclusions, tour.Highlights,
                tour.TravellerRequirements, tour.TimeZoneId ?? Tour.DefaultTimeZoneId, destinationIdsJson,
                tour.DestinationSummary, tour.CancellationPolicyTemplateId ?? 0, itineraryItemsJson,
                schedulesJson, now);
            dbContext.TourPendingChangeSets.Add(changeSet);
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorId,
                AuditActionTypes.TourPendingChangeCreated, AuditEntityTypes.Tour, tour.Id, now,
                AuditOutcome.Success));
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(new PendingChangeResponse(
                changeSet.Id, changeSet.TourId, changeSet.Status.ToString(), IsNew: true));
        }, cancellationToken);
    }

    private static Result<PendingChangeResponse> AccessDenied() =>
        Result.Failure<PendingChangeResponse>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}
