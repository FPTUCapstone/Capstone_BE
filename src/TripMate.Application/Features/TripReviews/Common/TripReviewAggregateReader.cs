using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Common.Models;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripReviews.Common;

public static class TripReviewAggregateReader
{
    public sealed class PoiContribution
    {
        public long TargetId { get; init; }
        public byte Rating { get; init; }
    }

    public static async Task<Result<TripReviewAggregateDto>> ReadTourAsync(
        IApplicationDbContext dbContext,
        long tourId,
        ILogger integrityLogger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrityLogger);

        var hasOverlap = await (
            from current in dbContext.TripReviews.AsNoTracking()
            join legacy in dbContext.Reviews.AsNoTracking()
                on current.BookingId equals legacy.BookingId
            where current.PublicationStatus == TripReview.PublishedStatus
                && (current.TourId == tourId
                    || (legacy.TargetType == Review.TargetTypeTour
                        && legacy.TargetId == tourId))
            select current.Id)
            .AnyAsync(cancellationToken);

        if (hasOverlap)
        {
            integrityLogger.LogWarning(
                "TM-79 aggregate integrity overlap: canonical review won over legacy Tour review for Tour {TourId}.",
                tourId);
        }

        var ratings = dbContext.TripReviews.AsNoTracking()
            .Where(review => review.TourId == tourId
                && review.PublicationStatus == TripReview.PublishedStatus)
            .Select(review => (decimal)review.OverallRating)
            .Concat(dbContext.Reviews.AsNoTracking()
                .Where(review => review.TargetType == Review.TargetTypeTour
                    && review.TargetId == tourId
                    && (!review.BookingId.HasValue
                        || !dbContext.TripReviews.Any(current =>
                            current.PublicationStatus == TripReview.PublishedStatus
                            && current.BookingId == review.BookingId)))
                .Select(review => (decimal)review.Rating));

        var aggregate = await ratings.GroupBy(_ => 1)
            .Select(group => new TripReviewAggregateDto(
                group.Average(), group.Count()))
            .FirstOrDefaultAsync(cancellationToken);

        return Result.Success(aggregate is null
            ? TripReviewAggregateDto.Empty
            : aggregate with { Average = TripReviewAggregateDto.Round(aggregate.Average) });
    }

    public static IQueryable<PoiContribution> QueryPoiReviews(
        IApplicationDbContext dbContext)
    {
        var canonical = dbContext.TripReviews.AsNoTracking()
            .Where(review => review.PoiId.HasValue
                && review.PublicationStatus == TripReview.PublishedStatus)
            .Select(review => new PoiContribution
            {
                TargetId = review.PoiId!.Value,
                Rating = review.OverallRating,
            });

        var legacy = dbContext.Reviews.AsNoTracking()
            .Where(review => review.TargetType == Review.TargetTypePoi
                && (!review.BookingId.HasValue
                    || !dbContext.TripReviews.Any(current =>
                        current.PublicationStatus == TripReview.PublishedStatus
                        && current.BookingId == review.BookingId)))
            .Select(review => new PoiContribution
            {
                TargetId = review.TargetId,
                Rating = review.Rating,
            });

        // Concat intentionally preserves equal-valued legitimate rows as UNION ALL.
        // Canonical wins for a typed commerce-booking overlap; the conflict query
        // keeps excluded rows observable for internal integrity reporting.
        return canonical.Concat(legacy);
    }

    public static IQueryable<long> QueryConflictingLegacyTourTargetIds(
        IApplicationDbContext dbContext) =>
        from legacy in dbContext.Reviews.AsNoTracking()
        where legacy.TargetType == Review.TargetTypeTour && legacy.BookingId.HasValue
        join current in dbContext.TripReviews.AsNoTracking()
            on legacy.BookingId equals current.BookingId
        where current.PublicationStatus == TripReview.PublishedStatus
        select legacy.TargetId;

    public static IQueryable<long> QueryConflictingLegacyPoiTargetIds(
        IApplicationDbContext dbContext) =>
        from legacy in dbContext.Reviews.AsNoTracking()
        where legacy.TargetType == Review.TargetTypePoi && legacy.BookingId.HasValue
        join current in dbContext.TripReviews.AsNoTracking()
            on legacy.BookingId equals (long?)current.BookingId
        where current.PublicationStatus == TripReview.PublishedStatus
        select legacy.TargetId;
}