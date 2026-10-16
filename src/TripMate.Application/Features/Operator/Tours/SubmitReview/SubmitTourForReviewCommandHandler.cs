using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Operator.Tours.SubmitReview;

public sealed class SubmitTourForReviewCommandHandler(
    IApplicationDbContext dbContext,
    ICurrentUserService currentUser,
    IDateTimeProvider clock)
    : IRequestHandler<SubmitTourForReviewCommand, Result<SubmitTourForReviewResponse>>
{
    public async Task<Result<SubmitTourForReviewResponse>> Handle(
        SubmitTourForReviewCommand request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue)
        {
            return AccessDenied();
        }

        long operatorId = currentUser.UserId.Value;
        return await dbContext.ExecuteInSerializableTransactionAsync(async transactionCancellationToken =>
        {
            bool eligible = await dbContext.OperatorProfiles.AsNoTracking().AnyAsync(profile =>
                profile.UserId == operatorId &&
                profile.ApprovalStatus == OperatorApprovalStatus.Approved &&
                profile.User.Role == UserRole.TourOperator &&
                profile.User.Status == AccountStatus.Active,
                transactionCancellationToken);
            if (!eligible)
            {
                return AccessDenied();
            }

            Tour? tour = await dbContext.Tours.SingleOrDefaultAsync(candidate =>
                candidate.Id == request.TourId && candidate.OperatorUserId == operatorId,
                transactionCancellationToken);
            if (tour is null)
            {
                return Result.Failure<SubmitTourForReviewResponse>(
                    OperatorTourErrorCodes.TourNotFound,
                    "The tour is unavailable.");
            }

            if (tour.Status is not (TourStatus.Draft or TourStatus.Rejected))
            {
                return Result.Failure<SubmitTourForReviewResponse>(
                    OperatorTourErrorCodes.InvalidState,
                    "Only a Draft or Rejected tour can be submitted for review.");
            }

            DateTimeOffset now = clock.UtcNow;
            var readiness = await TourReviewReadinessEvaluator.LoadAsync(
                dbContext, tour.Id, tour.CancellationPolicyTemplateId, now, transactionCancellationToken);
            if (!readiness.IsReady)
            {
                return Result.Failure<SubmitTourForReviewResponse>(
                    OperatorTourErrorCodes.ReviewPrerequisitesMissing,
                    "A primary image, a destination, a priced future departure, and cancellation terms are required before review.");
            }

            TourStatus statusBefore = tour.Status;
            tour.SubmitForReview(now);
            dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                operatorId,
                AuditActionTypes.TourSubmitForReview,
                AuditEntityTypes.Tour,
                tour.Id,
                now,
                AuditOutcome.Success,
                beforeData: JsonSerializer.Serialize(new { Status = statusBefore.ToString() }),
                afterData: JsonSerializer.Serialize(new { Status = TourStatus.Pending.ToString() })));
            await dbContext.SaveChangesAsync(transactionCancellationToken);
            return Result.Success(new SubmitTourForReviewResponse(tour.Id, nameof(TourStatus.Pending)));
        }, cancellationToken);
    }

    private static Result<SubmitTourForReviewResponse> AccessDenied() =>
        Result.Failure<SubmitTourForReviewResponse>(
            OperatorTourErrorCodes.AccessRequired,
            "An active approved Tour Operator account is required.");
}