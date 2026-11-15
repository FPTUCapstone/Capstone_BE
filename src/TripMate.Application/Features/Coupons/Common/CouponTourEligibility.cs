using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Coupons.Common;

internal static class CouponTourEligibility
{
    public static bool IsEligible(Tour tour, DateTimeOffset nowUtc) =>
        tour.Status == TourStatus.Published ||
        (tour.Status == TourStatus.Approved && tour.PublishedAtUtc.HasValue && tour.PublishedAtUtc <= nowUtc);
}