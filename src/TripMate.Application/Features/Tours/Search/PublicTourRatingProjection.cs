using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Tours.Search;

/// <summary>
/// UC-24-only rating source. Canonical Published reviews are authoritative;
/// legacy social.Reviews are intentionally excluded because publication provenance is absent.
/// </summary>
public static class PublicTourRatingProjection
{
    public static IQueryable<TourRatingAggregateProjection> Query(
        IApplicationDbContext dbContext,
        IQueryable<Tour> tours)
    {
        var tourIds = tours.Select(tour => tour.Id).Distinct();
        var aggregates = dbContext.TripReviews
            .AsNoTracking()
            .Where(review => review.TourId.HasValue
                && review.PublicationStatus == TripReview.PublishedStatus
                && tourIds.Contains(review.TourId.Value))
            .GroupBy(review => review.TourId!.Value)
            .Select(group => new
            {
                TourId = group.Key,
                RawAverage = (decimal?)group.Average(review => (decimal)review.OverallRating),
                ReviewCount = (int?)group.Count(),
            });

        return from tourId in tourIds
               join aggregate in aggregates on tourId equals aggregate.TourId into matching
               from aggregate in matching.DefaultIfEmpty()
               select new TourRatingAggregateProjection
               {
                   TourId = tourId,
                   RawAverage = aggregate.RawAverage,
                   ReviewCount = aggregate.ReviewCount ?? 0,
               };
    }
}