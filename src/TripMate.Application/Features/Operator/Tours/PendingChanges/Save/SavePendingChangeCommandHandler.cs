using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Save;

/// <summary>
/// Saves changes to an open (Draft/Rejected) draft version (spec TM-82 §5.3). Enforces BR-61
/// (capacity vs. sold slots), the schedule-id-ownership check (spec §3 point G6), and BR-22's
/// exemption for past-departure schedules (spec §3 point D) against the live Tour's own schedules.
/// </summary>
public sealed class SavePendingChangeCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<SavePendingChangeCommand, Result<PendingChangeSavedResponse>>
{
    public async Task<Result<PendingChangeSavedResponse>> Handle(
        SavePendingChangeCommand request, CancellationToken cancellationToken)
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
                .Include(candidate => candidate.Schedules)
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == request.TourId && candidate.OperatorUserId == operatorId,
                    transactionCancellationToken);
            if (tour is null)
                return Result.Failure<PendingChangeSavedResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");

            TourPendingChangeSet? changeSet = await dbContext.TourPendingChangeSets
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == request.PendingChangeId && candidate.TourId == tour.Id,
                    transactionCancellationToken);
            if (changeSet is null)
            {
                return Result.Failure<PendingChangeSavedResponse>(
                    OperatorTourErrorCodes.PendingChangeNotFound, "The draft version is unavailable.");
            }
            if (changeSet.Status is not (TourPendingChangeStatus.Draft or TourPendingChangeStatus.Rejected))
            {
                return Result.Failure<PendingChangeSavedResponse>(OperatorTourErrorCodes.InvalidState,
                    "MSG122: the draft version is currently pending approval.");
            }

            var liveSchedulesById = tour.Schedules.ToDictionary(schedule => schedule.Id);
            DateTimeOffset now = clock.UtcNow;
            var effectiveSchedules = new List<PendingChangeScheduleItem>(request.Schedules.Count);
            var pastDepartureScheduleIdsSeen = new HashSet<long>();

            foreach (var proposed in request.Schedules)
            {
                if (proposed.ScheduleId is { } scheduleId)
                {
                    if (!liveSchedulesById.TryGetValue(scheduleId, out var liveSchedule))
                    {
                        return Result.Failure<PendingChangeSavedResponse>(
                            OperatorTourErrorCodes.ScheduleNotOwnedByTour,
                            "A proposed schedule does not belong to this tour.");
                    }

                    // spec §3 point D: a past-departure schedule is always carried over verbatim
                    // with its ORIGINAL field values — never re-validated against BR-22, and never
                    // allowed to drift (capacity, price, meeting point, etc.) just because the
                    // request body still includes it. The live row's values always win here,
                    // regardless of what the operator proposed.
                    if (liveSchedule.StartAtUtc <= now)
                    {
                        pastDepartureScheduleIdsSeen.Add(liveSchedule.Id);
                        effectiveSchedules.Add(new PendingChangeScheduleItem(
                            liveSchedule.Id, liveSchedule.StartAtUtc, liveSchedule.EndAtUtc,
                            liveSchedule.MeetingPoint, liveSchedule.TotalCapacity, liveSchedule.MinimumCapacity,
                            liveSchedule.RegistrationDeadlineAtUtc, liveSchedule.AdultPrice,
                            liveSchedule.ChildPrice, liveSchedule.InfantPrice, liveSchedule.MeetingLatitude,
                            liveSchedule.MeetingLongitude));
                        continue;
                    }

                    if (proposed.StartAtUtc <= now)
                    {
                        return Result.Failure<PendingChangeSavedResponse>(
                            OperatorTourErrorCodes.InvalidScheduleDate,
                            "MSG29: the departure date must not be earlier than the current date.");
                    }
                    if (proposed.TotalCapacity < liveSchedule.ReservedCapacity)
                    {
                        return Result.Failure<PendingChangeSavedResponse>(
                            OperatorTourErrorCodes.ScheduleHasActiveBookings,
                            "MSG143: the capacity cannot be reduced below the number of slots already sold.");
                    }
                    effectiveSchedules.Add(proposed);
                }
                else
                {
                    if (proposed.StartAtUtc <= now)
                    {
                        return Result.Failure<PendingChangeSavedResponse>(
                            OperatorTourErrorCodes.InvalidScheduleDate,
                            "MSG29: the departure date must not be earlier than the current date.");
                    }
                    effectiveSchedules.Add(proposed);
                }
            }

            // spec §3 point D: a past-departure schedule can never be removed from the draft by
            // omitting it from the request body — it is always present in the saved payload,
            // regardless of what the operator submitted.
            foreach (var liveSchedule in tour.Schedules)
            {
                if (liveSchedule.StartAtUtc <= now && pastDepartureScheduleIdsSeen.Add(liveSchedule.Id))
                {
                    effectiveSchedules.Add(new PendingChangeScheduleItem(
                        liveSchedule.Id, liveSchedule.StartAtUtc, liveSchedule.EndAtUtc,
                        liveSchedule.MeetingPoint, liveSchedule.TotalCapacity, liveSchedule.MinimumCapacity,
                        liveSchedule.RegistrationDeadlineAtUtc, liveSchedule.AdultPrice,
                        liveSchedule.ChildPrice, liveSchedule.InfantPrice, liveSchedule.MeetingLatitude,
                        liveSchedule.MeetingLongitude));
                }
            }

            long[] destinationIds = request.DestinationIds.ToArray();
            var destinationNames = await dbContext.Destinations.AsNoTracking()
                .Where(destination => destinationIds.Contains(destination.Id))
                .ToDictionaryAsync(destination => destination.Id, destination => destination.Name,
                    transactionCancellationToken);
            if (destinationNames.Count != destinationIds.Length)
            {
                return Result.Failure<PendingChangeSavedResponse>(OperatorTourErrorCodes.DestinationNotFound,
                    "One or more selected destinations are unavailable.");
            }

            string destinationIdsJson = PendingChangePayload.SerializeDestinationIds(request.DestinationIds);
            string itineraryItemsJson = PendingChangePayload.SerializeItineraryItems(request.ItineraryItems);
            string schedulesJson = PendingChangePayload.SerializeSchedules(effectiveSchedules);
            string destinationSummary = OperatorTourValidation.BuildDestinationSummary(
                destinationIds.Select(id => destinationNames[id]));

            changeSet.SaveDraft(
                request.CategoryId ?? 0, request.Title, request.Description, request.BasePrice,
                request.DurationDays, request.Inclusions, request.Exclusions, request.Highlights,
                request.TravellerRequirements, request.TimeZoneId ?? Tour.DefaultTimeZoneId,
                destinationIdsJson, destinationSummary,
                request.CancellationPolicyTemplateId, itineraryItemsJson, schedulesJson, now);

            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorId,
                AuditActionTypes.TourPendingChangeSaved, AuditEntityTypes.Tour, tour.Id, now,
                AuditOutcome.Success));

            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(new PendingChangeSavedResponse(
                changeSet.Id, changeSet.TourId, changeSet.Status.ToString()));
        }, cancellationToken);
    }

    private static Result<PendingChangeSavedResponse> AccessDenied() =>
        Result.Failure<PendingChangeSavedResponse>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}