using MediatR;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Admin.Tours.Review.PendingChangesQueue;

/// <summary>
/// Combines first-time Pending tours and PendingReview draft versions into one paginated list
/// (spec TM-82 §5.6). Each side is queried and ordered by SubmittedAtUtc/UpdatedAtUtc separately
/// (EF can translate each one on its own; a single SQL-translatable UNION across the two distinct
/// entity shapes is not practical here), then the two ordered, bounded candidate sets are merged
/// and paged in memory — acceptable for a moderation queue's expected size, not a full-table scan.
/// </summary>
public sealed class GetModerationQueueQueryHandler(IApplicationDbContext dbContext, ICurrentUserService currentUser)
    : IRequestHandler<GetModerationQueueQuery, Result<ModerationQueueResponse>>
{
    public async Task<Result<ModerationQueueResponse>> Handle(
        GetModerationQueueQuery request, CancellationToken cancellationToken)
    {
        if (!await TourReviewReadAccess.IsActiveStaffAsync(dbContext, currentUser, cancellationToken))
        {
            return Result.Failure<ModerationQueueResponse>(
                OperatorTourErrorCodes.AdminAccessRequired, "Active Staff access is required.");
        }

        int newSubmissionCount = await dbContext.Tours.AsNoTracking()
            .CountAsync(tour => tour.Status == TourStatus.Pending, cancellationToken);
        int draftVersionCount = await dbContext.TourPendingChangeSets.AsNoTracking()
            .CountAsync(changeSet => changeSet.Status == TourPendingChangeStatus.PendingReview, cancellationToken);
        int totalCount = newSubmissionCount + draftVersionCount;

        int boundedWindow = request.Page * request.PageSize;
        var newSubmissions = await dbContext.Tours.AsNoTracking()
            .Where(tour => tour.Status == TourStatus.Pending)
            .OrderBy(tour => tour.SubmittedAtUtc).ThenBy(tour => tour.Id)
            .Take(boundedWindow)
            .Select(tour => new ModerationQueueItem(
                tour.Id, tour.Id, ModerationQueueKinds.NewSubmission, tour.OperatorUserId, tour.Title,
                tour.Status.ToString(), tour.SubmittedAtUtc))
            .ToListAsync(cancellationToken);
        var draftVersions = await dbContext.TourPendingChangeSets.AsNoTracking()
            .Where(changeSet => changeSet.Status == TourPendingChangeStatus.PendingReview)
            .OrderBy(changeSet => changeSet.SubmittedAtUtc).ThenBy(changeSet => changeSet.Id)
            .Take(boundedWindow)
            .Select(changeSet => new ModerationQueueItem(
                changeSet.Id, changeSet.TourId, ModerationQueueKinds.DraftVersion,
                changeSet.SubmittedByUserId, changeSet.Title, changeSet.Status.ToString(),
                changeSet.SubmittedAtUtc))
            .ToListAsync(cancellationToken);

        var merged = newSubmissions.Concat(draftVersions)
            .OrderBy(item => item.SubmittedAtUtc)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToList();

        return Result.Success(new ModerationQueueResponse(merged, request.Page, request.PageSize, totalCount));
    }
}
