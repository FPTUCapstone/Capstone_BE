using TripMate.Application.Features.TripReviews.Common;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>
/// Wire values for the <c>kind</c> half of a Trip History record identity. A record is identified by
/// (kind, id); the numeric id alone is never meaningful.
/// </summary>
public static class TripRecordKinds
{
    /// <summary>Same wire value as <see cref="ReviewableRecordRef.CommerceBookingKind"/> (TM-79).</summary>
    public const string CommerceBooking = ReviewableRecordRef.CommerceBookingKind;

    public const string Itinerary = "itinerary";

    public static readonly string[] All = [CommerceBooking, Itinerary];
}