using System.Data.Common;
using System.Text.Json;

using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Tours.Review;

public sealed class ApproveTourCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<ApproveTourCommand, Result<TourReviewResponse>>
{
    public async Task<Result<TourReviewResponse>> Handle(ApproveTourCommand request, CancellationToken cancellationToken)
    {
        var access = AuthorizeStaff();
        if (access is not null) return access;

        if (request.Checklist is null ||
            !request.Checklist.ContentCompleteness ||
            !request.Checklist.ImageAppropriateness ||
            !request.Checklist.PricePlausibility ||
            !request.Checklist.PolicyCompliance ||
            !request.Checklist.ItineraryFeasibility)
        {
            return Result.Failure<TourReviewResponse>(
                OperatorTourErrorCodes.ReviewChecklistIncomplete,
                "Every item of the mandatory review checklist must pass before the approval can be confirmed.");
        }

        const int maxConcurrencyAttempts = 3;
        for (var attempt = 1; attempt <= maxConcurrencyAttempts; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(async ct =>
                {
                    var tour = await dbContext.Tours.SingleOrDefaultAsync(x => x.Id == request.TourId, ct);
                    if (tour is null) return NotFound();
                    if (tour.Status != TourStatus.Pending) return InvalidState();

                    var now = clock.UtcNow;
                    var reviewerId = currentUser.UserId!.Value;
                    var statusBefore = tour.Status;

                    tour.Approve(reviewerId, now, publishedAtUtc: now);

                    var afterData = JsonSerializer.Serialize(new
                    {
                        Status = tour.Status.ToString(),
                        VersionNo = tour.VersionNo,
                        VersionRootTourId = tour.VersionRootTourId,
                        ReviewedBy = reviewerId,
                        ReviewedAtUtc = now,
                        PublishedAtUtc = tour.PublishedAtUtc,
                        Checklist = request.Checklist,
                        InternalNote = request.Note,
                    });

                    dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                        reviewerId,
                        AuditActionTypes.TourApproved,
                        AuditEntityTypes.Tour,
                        tour.Id,
                        now,
                        AuditOutcome.Success,
                        beforeData: JsonSerializer.Serialize(new { Status = statusBefore.ToString() }),
                        afterData: afterData));

                    await dbContext.SaveChangesAsync(ct);
                    return Result.Success(new TourReviewResponse(tour.Id, tour.Status.ToString(), now));
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < maxConcurrencyAttempts && dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
                var delayMs = Random.Shared.Next(20, 50) * attempt;
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                dbContext.ClearTrackedEntities();
                return ServiceUnavailable();
            }
        }

        return ServiceUnavailable();
    }

    private bool IsPersistenceFailure(Exception exception)
    {
        if (dbContext.IsDeadlock(exception))
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateException or DbException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private Result<TourReviewResponse>? AuthorizeStaff() => currentUser.UserId is null || currentUser.Role != nameof(UserRole.Staff)
        ? Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Staff access is required.") : null;
    private static Result<TourReviewResponse> NotFound() => Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
    private static Result<TourReviewResponse> InvalidState() => Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.InvalidState, "The selected package is no longer in the Pending Approval status.");
    private static Result<TourReviewResponse> ServiceUnavailable() => Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.ServiceUnavailable, "TripMate is temporarily unable to process your request. Please check your connection and try again.");
}

public sealed class RejectTourCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<RejectTourCommand, Result<TourReviewResponse>>
{
    public async Task<Result<TourReviewResponse>> Handle(RejectTourCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Staff))
            return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Staff access is required.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > Tour.RejectionReasonMaxLength)
            return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.RejectionReasonInvalid, "A rejection reason of at most 500 characters is required.");
        var tour = await dbContext.Tours.SingleOrDefaultAsync(x => x.Id == request.TourId, cancellationToken);
        if (tour is null) return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
        if (tour.Status != TourStatus.Pending) return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.InvalidState, "The tour is not awaiting review.");
        var now = clock.UtcNow;
        tour.Reject(currentUser.UserId.Value, request.Reason, now);
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(currentUser.UserId.Value, AuditActionTypes.TourRejected, AuditEntityTypes.Tour, tour.Id, now, AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { Status = TourStatus.Pending.ToString() }),
            afterData: JsonSerializer.Serialize(new { Status = tour.Status.ToString(), tour.RejectionReason })));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(new TourReviewResponse(tour.Id, tour.Status.ToString(), now));
    }
}

public sealed class PublishTourCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<PublishTourCommand, Result<TourReviewResponse>>
{
    public async Task<Result<TourReviewResponse>> Handle(PublishTourCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Staff))
            return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Staff access is required.");
        var tour = await dbContext.Tours.SingleOrDefaultAsync(x => x.Id == request.TourId, cancellationToken);
        if (tour is null) return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
        if (tour.Status != TourStatus.Approved) return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.InvalidState, "Only Approved tours can be published.");
        var now = clock.UtcNow;
        tour.Publish(currentUser.UserId.Value, now);
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(currentUser.UserId.Value, AuditActionTypes.TourPublished, AuditEntityTypes.Tour, tour.Id, now, AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { Status = TourStatus.Approved.ToString() }),
            afterData: JsonSerializer.Serialize(new { Status = tour.Status.ToString() })));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(new TourReviewResponse(tour.Id, tour.Status.ToString(), now));
    }
}

public sealed class UnpublishTourCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<UnpublishTourCommand, Result<TourReviewResponse>>
{
    public async Task<Result<TourReviewResponse>> Handle(UnpublishTourCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Staff))
            return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Staff access is required.");

        const int maxConcurrencyAttempts = 3;
        for (var attempt = 1; attempt <= maxConcurrencyAttempts; attempt++)
        {
            try
            {
                return await dbContext.ExecuteInSerializableTransactionAsync(async ct =>
                {
                    var tour = await dbContext.Tours.SingleOrDefaultAsync(x => x.Id == request.TourId, ct);
                    if (tour is null)
                        return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");

                    var now = clock.UtcNow;
                    if (!tour.CanUnpublish(now))
                        return Result.Failure<TourReviewResponse>(
                            OperatorTourErrorCodes.InvalidState,
                            "Only Published or publicly active Approved tours can be unpublished.");

                    var moderatorId = currentUser.UserId.Value;
                    var statusBefore = tour.Status;
                    tour.Unpublish(now);

                    dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(
                        moderatorId,
                        AuditActionTypes.TourUnpublished,
                        AuditEntityTypes.Tour,
                        tour.Id,
                        now,
                        AuditOutcome.Success,
                        beforeData: JsonSerializer.Serialize(new
                        {
                            Status = statusBefore.ToString(),
                            PublishedAtUtc = tour.PublishedAtUtc,
                        }),
                        afterData: JsonSerializer.Serialize(new
                        {
                            Status = tour.Status.ToString(),
                            VersionNo = tour.VersionNo,
                            VersionRootTourId = tour.VersionRootTourId,
                            ModeratedBy = moderatorId,
                            UnpublishedAtUtc = now,
                            PublishedAtUtc = tour.PublishedAtUtc,
                        })));

                    await dbContext.SaveChangesAsync(ct);
                    return Result.Success(new TourReviewResponse(tour.Id, tour.Status.ToString(), now));
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < maxConcurrencyAttempts && dbContext.IsDeadlock(exception))
            {
                dbContext.ClearTrackedEntities();
                var delayMs = Random.Shared.Next(20, 50) * attempt;
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                dbContext.ClearTrackedEntities();
                return ServiceUnavailable();
            }
        }

        return ServiceUnavailable();
    }

    private bool IsPersistenceFailure(Exception exception)
    {
        if (dbContext.IsDeadlock(exception))
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateException or DbException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private static Result<TourReviewResponse> ServiceUnavailable() =>
        Result.Failure<TourReviewResponse>(
            OperatorTourErrorCodes.ServiceUnavailable,
            "TripMate is temporarily unable to process your request. Please check your connection and try again.");
}