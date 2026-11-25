using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Approve;

/// <summary>
/// Approves a PendingReview draft version (spec TM-82 §5.7). The decision, the live Tour mutation,
/// and the audit log all commit together in one serializable transaction; the in-app notification
/// is attempted only after that commit (spec §3 point B/G5), via <see cref="TourPendingChangeNotices"/>.
/// </summary>
public sealed class ApprovePendingChangeCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    ILogger<ApprovePendingChangeCommandHandler> logger)
    : IRequestHandler<ApprovePendingChangeCommand, Result<PendingChangeDecisionResponse>>
{
    public async Task<Result<PendingChangeDecisionResponse>> Handle(
        ApprovePendingChangeCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Staff))
        {
            return Result.Failure<PendingChangeDecisionResponse>(
                OperatorTourErrorCodes.AdminAccessRequired, "Staff access is required.");
        }
        long adminUserId = currentUser.UserId.Value;

        long submittedByUserId = 0;
        Result<PendingChangeDecisionResponse> decision = await dbContext.ExecuteInSerializableTransactionAsync(
            async transactionCancellationToken =>
        {
            TourPendingChangeSet? changeSet = await dbContext.TourPendingChangeSets
                .SingleOrDefaultAsync(candidate => candidate.Id == request.PendingChangeId,
                    transactionCancellationToken);
            if (changeSet is null)
            {
                return Result.Failure<PendingChangeDecisionResponse>(
                    OperatorTourErrorCodes.PendingChangeNotFound, "The draft version is unavailable.");
            }
            if (changeSet.Status != TourPendingChangeStatus.PendingReview)
            {
                return Result.Failure<PendingChangeDecisionResponse>(OperatorTourErrorCodes.InvalidState,
                    "MSG133: the draft version is no longer pending approval.");
            }

            Tour? tour = await dbContext.Tours
                .Include(candidate => candidate.Destinations)
                .Include(candidate => candidate.ItineraryItems)
                .Include(candidate => candidate.Schedules)
                .SingleOrDefaultAsync(candidate => candidate.Id == changeSet.TourId, transactionCancellationToken);
            if (tour is null || tour.Status is not (TourStatus.Approved or TourStatus.Published))
            {
                return Result.Failure<PendingChangeDecisionResponse>(OperatorTourErrorCodes.InvalidState,
                    "The live tour is no longer Approved or Published.");
            }

            var liveSchedulesById = tour.Schedules.ToDictionary(schedule => schedule.Id);
            var proposedSchedules = PendingChangePayload.DeserializeSchedules(changeSet.SchedulesJson);
            foreach (var proposed in proposedSchedules)
            {
                if (proposed.ScheduleId is { } scheduleId
                    && liveSchedulesById.TryGetValue(scheduleId, out var liveSchedule)
                    && proposed.TotalCapacity < liveSchedule.ReservedCapacity)
                {
                    return Result.Failure<PendingChangeDecisionResponse>(
                        OperatorTourErrorCodes.ScheduleHasActiveBookings,
                        "MSG143: the capacity cannot be reduced below the number of slots already sold.");
                }
            }

            CancellationPolicyTemplate? policy = await dbContext.CancellationPolicyTemplates
                .Include(template => template.Rules)
                .SingleOrDefaultAsync(template => template.Id == changeSet.CancellationPolicyTemplateId,
                    transactionCancellationToken);
            if (policy is null)
            {
                return Result.Failure<PendingChangeDecisionResponse>(
                    OperatorTourErrorCodes.CancellationPolicyUnavailable, "The selected cancellation policy is unavailable.");
            }

            DateTimeOffset now = clock.UtcNow;
            var proposedScheduleEntities = proposedSchedules.Select(proposed =>
            {
                var schedule = TourSchedule.Create(proposed.StartAtUtc, proposed.EndAtUtc,
                    proposed.MeetingPoint, proposed.TotalCapacity, now, proposed.MinimumCapacity,
                    proposed.RegistrationDeadlineAtUtc, proposed.AdultPrice, proposed.ChildPrice,
                    proposed.InfantPrice, proposed.MeetingLatitude, proposed.MeetingLongitude);
                schedule.Id = proposed.ScheduleId ?? 0;
                return schedule;
            }).ToList();
            var proposedItineraryItems = PendingChangePayload
                .DeserializeItineraryItems(changeSet.ItineraryItemsJson)
                .Select(item => TourItineraryItem.Create(item.DayNo, item.SequenceNo, item.PoiId,
                    item.PlannedTime, item.StayDurationMinutes, item.Notes, item.IsRequiredForWeather))
                .ToList();
            var destinationIds = PendingChangePayload.DeserializeDestinationIds(changeSet.DestinationIdsJson);

            tour.ApplyPendingChange(
                changeSet.CategoryId == 0 ? null : changeSet.CategoryId, changeSet.Title, changeSet.Description,
                changeSet.BasePrice, changeSet.DurationDays, changeSet.Inclusions, changeSet.Exclusions,
                changeSet.Highlights, changeSet.TravellerRequirements, changeSet.TimeZoneId, destinationIds,
                changeSet.DestinationSummary, policy, proposedItineraryItems, proposedScheduleEntities, now);

            changeSet.MarkApplied(adminUserId, now);
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(adminUserId,
                AuditActionTypes.TourPendingChangeApproved, AuditEntityTypes.Tour, tour.Id, now,
                AuditOutcome.Success));
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            submittedByUserId = changeSet.SubmittedByUserId;
            return Result.Success(new PendingChangeDecisionResponse(
                changeSet.Id, tour.Id, changeSet.Status.ToString(), tour.Status.ToString()));
        }, cancellationToken);

        if (decision.IsSuccess)
        {
            await TourPendingChangeNotices.NotifyOperatorAsync(
                dbContext, logger, submittedByUserId,
                Notification.TourPendingChangeApprovedType, Notification.TourPendingChangeApprovedTitle,
                decision.Value.Id, adminUserId, clock.UtcNow, cancellationToken);
        }

        return decision;
    }
}
