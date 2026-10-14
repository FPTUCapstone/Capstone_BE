using TripMate.Domain.Entities;

namespace TripMate.Application.Features.TripReviews.Common;

public enum TripReviewLegacyDisposition
{
    None,
    Duplicate,
    Conflict,
}

public static class TripReviewLegacyClassifier
{
    public static TripReviewLegacyDisposition Classify(TripReviewContextData data)
    {
        if (data.HasForeignLegacyAuthor)
            return TripReviewLegacyDisposition.Conflict;

        if (data.OwnedLegacyEntries.Count == 0)
            return TripReviewLegacyDisposition.None;

        if (data.Parent is not null || data.OwnedLegacyEntries.Count != 1)
            return TripReviewLegacyDisposition.Conflict;

        var entry = data.OwnedLegacyEntries[0];
        if (entry.TargetId <= 0)
            return TripReviewLegacyDisposition.Conflict;

        // A legacy Tour row can be verified against the booking-derived Tour.
        // Other legacy target kinds remain a conservative single linked duplicate;
        // multiple rows need G-LEGACY reconciliation and are blocked above.
        if (entry.TargetType == Review.TargetTypeTour)
        {
            var expectedTourId = data.Booking.TourId ?? data.Booking.SourceTourId;
            if (!expectedTourId.HasValue || expectedTourId.Value != entry.TargetId)
                return TripReviewLegacyDisposition.Conflict;
        }
        else if (entry.TargetType is not (Review.TargetTypePoi or Review.TargetTypeRouteSegment
                     or Review.TargetTypeOperator))
        {
            return TripReviewLegacyDisposition.Conflict;
        }

        return TripReviewLegacyDisposition.Duplicate;
    }
}