using MediatR;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Approve;
using TripMate.Application.Features.Operator.Tours.PendingChanges.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.PendingChanges.Reject;

/// <summary>
/// Rejects a PendingReview draft version (spec TM-82 §5.8). Mirrors
/// <see cref="Approve.ApprovePendingChangeCommandHandler"/> minus the live-tour mutation and the
/// BR-61 guard; the live Tour is never touched.
/// </summary>
public sealed class RejectPendingChangeCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock,
    ILogger<RejectPendingChangeCommandHandler> logger)
    : IRequestHandler<RejectPendingChangeCommand, Result<PendingChangeDecisionResponse>>
{
    public async Task<Result<PendingChangeDecisionResponse>> Handle(
        RejectPendingChangeCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Staff))
        {
            return Result.Failure<PendingChangeDecisionResponse>(
                OperatorTourErrorCodes.AdminAccessRequired, "Staff access is required.");
        }
        long adminUserId = currentUser.UserId.Value;

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > Tour.RejectionReasonMaxLength)
        {
            return Result.Failure<PendingChangeDecisionResponse>(
                OperatorTourErrorCodes.RejectionReasonInvalid, "MSG123: a valid rejection reason is required.");
        }

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

            Tour? tour = await dbContext.Tours.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == changeSet.TourId, transactionCancellationToken);
            if (tour is null)
            {
                return Result.Failure<PendingChangeDecisionResponse>(
                    OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
            }

            DateTimeOffset now = clock.UtcNow;
            changeSet.Reject(adminUserId, request.Reason, now);
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(adminUserId,
                AuditActionTypes.TourPendingChangeRejected, AuditEntityTypes.Tour, tour.Id, now,
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
                Notification.TourPendingChangeRejectedType, Notification.TourPendingChangeRejectedTitle,
                decision.Value.Id, adminUserId, clock.UtcNow, cancellationToken);
        }

        return decision;
    }
}
