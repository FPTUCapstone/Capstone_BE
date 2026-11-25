using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Submit;

/// <summary>
/// Submits a draft version for approval (spec TM-82 §5.5). BR-101 for a draft version uses only
/// the three plain SRS-named conditions (mandatory fields, at least one itinerary day, at least
/// one departure schedule) — spec §3 point H.D3 — deliberately not
/// TourReviewReadinessEvaluator's six facts, since that evaluator's primary-image fact does not
/// apply (a draft version has no media, spec §3 point H.D2).
/// </summary>
public sealed class SubmitPendingChangeCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<SubmitPendingChangeCommand, Result<PendingChangeSubmittedResponse>>
{
    public async Task<Result<PendingChangeSubmittedResponse>> Handle(
        SubmitPendingChangeCommand request, CancellationToken cancellationToken)
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

            Tour? tour = await dbContext.Tours.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.Id == request.TourId && candidate.OperatorUserId == operatorId,
                transactionCancellationToken);
            if (tour is null)
                return Result.Failure<PendingChangeSubmittedResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");

            TourPendingChangeSet? changeSet = await dbContext.TourPendingChangeSets.SingleOrDefaultAsync(
                candidate => candidate.Id == request.PendingChangeId && candidate.TourId == tour.Id,
                transactionCancellationToken);
            if (changeSet is null)
            {
                return Result.Failure<PendingChangeSubmittedResponse>(
                    OperatorTourErrorCodes.PendingChangeNotFound, "The draft version is unavailable.");
            }
            if (changeSet.Status is not (TourPendingChangeStatus.Draft or TourPendingChangeStatus.Rejected))
            {
                return Result.Failure<PendingChangeSubmittedResponse>(OperatorTourErrorCodes.InvalidState,
                    "MSG122: the draft version is currently pending approval.");
            }

            if (!IsReadyForReview(changeSet))
            {
                return Result.Failure<PendingChangeSubmittedResponse>(
                    OperatorTourErrorCodes.ReviewPrerequisitesMissing,
                    "MSG01: mandatory fields, at least one itinerary day, and at least one " +
                    "departure schedule are required.");
            }

            DateTimeOffset now = clock.UtcNow;
            changeSet.Submit(now);
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(operatorId,
                AuditActionTypes.TourPendingChangeSubmitted, AuditEntityTypes.Tour, tour.Id, now,
                AuditOutcome.Success));
            await dbContext.SaveChangesAsync(transactionCancellationToken);

            return Result.Success(new PendingChangeSubmittedResponse(
                changeSet.Id, changeSet.TourId, changeSet.Status.ToString()));
        }, cancellationToken);
    }

    private static bool IsReadyForReview(TourPendingChangeSet changeSet)
    {
        bool hasMandatoryFields = !string.IsNullOrWhiteSpace(changeSet.Title)
            && changeSet.Title.Trim().Length <= Tour.TitleMaxLength
            && !string.IsNullOrWhiteSpace(changeSet.Description)
            && changeSet.DurationDays > 0
            && changeSet.BasePrice > 0
            && changeSet.CancellationPolicyTemplateId > 0;
        bool hasItinerary = PendingChangePayload.DeserializeItineraryItems(changeSet.ItineraryItemsJson).Count > 0;
        bool hasSchedule = PendingChangePayload.DeserializeSchedules(changeSet.SchedulesJson).Count > 0;
        return hasMandatoryFields && hasItinerary && hasSchedule;
    }

    private static Result<PendingChangeSubmittedResponse> AccessDenied() =>
        Result.Failure<PendingChangeSubmittedResponse>(OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}
