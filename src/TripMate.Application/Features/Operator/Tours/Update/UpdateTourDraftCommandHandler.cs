using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.Update;

public sealed class UpdateTourDraftCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<UpdateTourDraftCommand, Result<UpdateTourDraftResponse>>
{
    public async Task<Result<UpdateTourDraftResponse>> Handle(
        UpdateTourDraftCommand request,
        CancellationToken cancellationToken)
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
                .Include(candidate => candidate.CancellationPolicyRules)
                .SingleOrDefaultAsync(candidate =>
                    candidate.Id == request.TourId && candidate.OperatorUserId == operatorId,
                    transactionCancellationToken);
            if (tour is null)
                return Result.Failure<UpdateTourDraftResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
            if (tour.Status is not (TourStatus.Draft or TourStatus.Rejected))
                return Result.Failure<UpdateTourDraftResponse>(OperatorTourErrorCodes.EditLocked, "Only Draft or Rejected tours can be edited.");

            if (request.CategoryId.HasValue && !await dbContext.TourCategories.AsNoTracking().AnyAsync(category =>
                    category.Id == request.CategoryId.Value && category.IsActive, transactionCancellationToken))
                return Result.Failure<UpdateTourDraftResponse>(OperatorTourErrorCodes.CategoryNotFound, "The selected tour category is unavailable.");

            long[] destinationIds = request.DestinationIds.ToArray();
            var destinationNames = await dbContext.Destinations.AsNoTracking()
                .Where(destination => destinationIds.Contains(destination.Id))
                .ToDictionaryAsync(destination => destination.Id, destination => destination.Name,
                    transactionCancellationToken);
            if (destinationNames.Count != destinationIds.Length)
                return Result.Failure<UpdateTourDraftResponse>(OperatorTourErrorCodes.DestinationNotFound,
                    "One or more selected destinations are unavailable.");

            CancellationPolicyTemplate? policy = await dbContext.CancellationPolicyTemplates
                .Include(template => template.Rules)
                .SingleOrDefaultAsync(template =>
                    template.Id == request.CancellationPolicyTemplateId && template.IsActive,
                    transactionCancellationToken);
            if (policy is null || policy.Rules.Count == 0)
                return Result.Failure<UpdateTourDraftResponse>(OperatorTourErrorCodes.CancellationPolicyUnavailable,
                    "The selected cancellation policy is unavailable.");

            long[] poiIds = request.ItineraryItems.Select(item => item.PoiId).Distinct().ToArray();
            int availablePoiCount = await dbContext.PointsOfInterest.AsNoTracking().CountAsync(poi =>
                poiIds.Contains(poi.Id) && poi.Status == PointOfInterestStatus.Active, transactionCancellationToken);
            if (availablePoiCount != poiIds.Length)
                return Result.Failure<UpdateTourDraftResponse>(OperatorTourErrorCodes.PoiNotFound, "One or more selected points of interest are unavailable.");

            DateTimeOffset now = clock.UtcNow;

            // Phase 1: delete the old children and save first. The children carry unique indexes
            // (destination sequence, itinerary day/sequence, policy cutoff), so deleting and
            // re-inserting equal keys in one SaveChanges is not safe. Both saves share the
            // serializable transaction, so readers never observe the intermediate state.
            dbContext.TourDestinations.RemoveRange(tour.Destinations.ToList());
            dbContext.TourItineraryItems.RemoveRange(tour.ItineraryItems.ToList());
            dbContext.TourSchedules.RemoveRange(tour.Schedules.ToList());
            dbContext.TourCancellationPolicyRules.RemoveRange(tour.CancellationPolicyRules.ToList());
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            // Phase 2: write the replacement children together with the scalar changes.
            tour.UpdateDraft(request.CategoryId, request.Title, request.Description,
                request.BasePrice, request.DurationDays,
                OperatorTourValidation.NormalizeText(request.Inclusions),
                OperatorTourValidation.NormalizeText(request.Exclusions), now,
                OperatorTourValidation.NormalizeText(request.Highlights),
                OperatorTourValidation.NormalizeText(request.TravellerRequirements),
                OperatorTourValidation.NormalizeText(request.TimeZoneId));
            var items = request.ItineraryItems.Select(item => TourItineraryItem.Create(
                item.DayNo, item.SequenceNo, item.PoiId, item.PlannedTime, item.StayDurationMinutes, item.Notes)).ToList();
            var schedules = request.Schedules.Select(schedule => TourSchedule.Create(schedule.StartAtUtc,
                schedule.EndAtUtc, schedule.MeetingPoint, schedule.TotalCapacity, now,
                schedule.MinimumCapacity, schedule.RegistrationDeadlineAtUtc,
                schedule.AdultPrice ?? request.BasePrice, schedule.ChildPrice, schedule.InfantPrice)).ToList();
            tour.ReplaceDraftChildren(items, schedules);
            tour.ReplaceDraftDestinations(destinationIds,
                OperatorTourValidation.BuildDestinationSummary(destinationIds.Select(id => destinationNames[id])));
            tour.ApplyCancellationPolicySnapshot(policy, now);
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorId,
                AuditActionTypes.TourDraftUpdated, AuditEntityTypes.Tour, tour.Id, now,
                AuditOutcome.Success, afterData: JsonSerializer.Serialize(new { tour.Title, tour.DurationDays })));
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return Result.Success(new UpdateTourDraftResponse(tour.Id, tour.Status.ToString()));
        }, cancellationToken);
    }

    private static Result<UpdateTourDraftResponse> AccessDenied() =>
        Result.Failure<UpdateTourDraftResponse>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}