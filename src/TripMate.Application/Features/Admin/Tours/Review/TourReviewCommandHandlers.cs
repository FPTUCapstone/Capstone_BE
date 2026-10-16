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
    public Task<Result<TourReviewResponse>> Handle(ApproveTourCommand request, CancellationToken cancellationToken) =>
        Transition(request.TourId, TourStatus.Approved, null, cancellationToken);

    private async Task<Result<TourReviewResponse>> Transition(long tourId, TourStatus target, string? reason, CancellationToken cancellationToken)
    {
        var access = AuthorizeAdmin();
        if (access is not null) return access;
        var tour = await dbContext.Tours.SingleOrDefaultAsync(x => x.Id == tourId, cancellationToken);
        if (tour is null) return NotFound();
        if (tour.Status != TourStatus.Pending) return InvalidState();
        var now = clock.UtcNow;
        tour.Approve(currentUser.UserId!.Value, now);
        await SaveAudit(tour, TourStatus.Pending, AuditActionTypes.TourApproved, now, null, cancellationToken);
        return Result.Success(new TourReviewResponse(tour.Id, tour.Status.ToString(), now));
    }

    private Result<TourReviewResponse>? AuthorizeAdmin() => currentUser.UserId is null || currentUser.Role != nameof(UserRole.Administrator)
        ? Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Administrator access is required.") : null;
    private static Result<TourReviewResponse> NotFound() => Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
    private static Result<TourReviewResponse> InvalidState() => Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.InvalidState, "The tour is not awaiting review.");
    private async Task SaveAudit(Tour tour, TourStatus before, string action, DateTimeOffset now, string? reason, CancellationToken ct)
    {
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(currentUser.UserId!.Value, action, AuditEntityTypes.Tour, tour.Id, now, AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { Status = before.ToString() }),
            afterData: JsonSerializer.Serialize(new { Status = tour.Status.ToString(), RejectionReason = reason })));
        await dbContext.SaveChangesAsync(ct);
    }
}

public sealed class RejectTourCommandHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser, IDateTimeProvider clock)
    : IRequestHandler<RejectTourCommand, Result<TourReviewResponse>>
{
    public async Task<Result<TourReviewResponse>> Handle(RejectTourCommand request, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Administrator access is required.");
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
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Administrator access is required.");
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
        if (currentUser.UserId is null || currentUser.Role != nameof(UserRole.Administrator))
            return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.AdminAccessRequired, "Administrator access is required.");
        var tour = await dbContext.Tours.SingleOrDefaultAsync(x => x.Id == request.TourId, cancellationToken);
        if (tour is null) return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.TourNotFound, "The tour is unavailable.");
        if (tour.Status != TourStatus.Published)
            return Result.Failure<TourReviewResponse>(OperatorTourErrorCodes.InvalidState, "Only Published tours can be unpublished.");
        var now = clock.UtcNow;
        tour.Unpublish(now);
        dbContext.AuditLogs.Add(AuditLog.CreateRecordedOutcome(currentUser.UserId.Value, AuditActionTypes.TourUnpublished, AuditEntityTypes.Tour, tour.Id, now, AuditOutcome.Success,
            beforeData: JsonSerializer.Serialize(new { Status = TourStatus.Published.ToString() }),
            afterData: JsonSerializer.Serialize(new { Status = tour.Status.ToString(), tour.PublishedAtUtc })));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success(new TourReviewResponse(tour.Id, tour.Status.ToString(), now));
    }
}
