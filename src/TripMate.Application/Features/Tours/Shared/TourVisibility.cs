using System.Linq.Expressions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Tours.Shared;

/// <summary>
/// The single definition of "a tour a Guest or Traveler may see" (BR-56) for the public detail read.
/// It mirrors the predicate used by UC-24 search; a parity test keeps the two from drifting.
/// </summary>
public static class TourVisibility
{
    /// <summary>
    /// Published, or approved with a publication date that has arrived, and owned by an approved,
    /// active operator. Drafts, pending, rejected, inactive and unpublished tours are never public.
    /// </summary>
    public static Expression<Func<Tour, bool>> PublicTour(DateTimeOffset nowUtc) =>
        tour => (tour.Status == TourStatus.Published
                 || (tour.Status == TourStatus.Approved
                     && tour.PublishedAtUtc.HasValue
                     && tour.PublishedAtUtc.Value <= nowUtc))
                && tour.OperatorProfile.ApprovalStatus == OperatorApprovalStatus.Approved
                && tour.OperatorProfile.User.Status == AccountStatus.Active;
}