using TripMate.Application.Features.TripHistory.List;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.TripHistory.Common;

/// <summary>
/// The single place that decides which tab a trip belongs to and which instant counts as its
/// departure (spec TM-78 P1, P2, P5). Pure: no I/O and no clock.
/// </summary>
public static class TripHistoryClassifier
{
    /// <summary>
    /// P1: by stored status only. A Confirmed booking whose schedule already ended stays Upcoming
    /// until something marks it Completed; dates are never used to guess.
    /// </summary>
    public static string TabForBooking(BookingStatus status) =>
        TryTabForBooking(status)
        ?? throw new ArgumentOutOfRangeException(nameof(status), status, "The booking status is not supported.");

    /// <summary>
    /// The tab of a status, or null for one not mapped yet, so a new status can only hide its own
    /// records and never break the whole list. An emergency-stopped booking did not complete, so
    /// it lists with the cancelled trips.
    /// </summary>
    public static string? TryTabForBooking(BookingStatus status) => status switch
    {
        BookingStatus.PendingPayment or BookingStatus.Confirmed => ListTravelerTripsQuery.TabUpcoming,
        BookingStatus.Completed => ListTravelerTripsQuery.TabCompleted,
        BookingStatus.Cancelled or BookingStatus.Expired or BookingStatus.Interrupted =>
            ListTravelerTripsQuery.TabCancelled,
        _ => null,
    };

    /// <summary>
    /// P2: a Completed trip session wins; otherwise Active is Upcoming and Cancelled is Cancelled.
    /// Returns null when the itinerary is not a trip (Draft, an unused Completed status without a
    /// session, or an unknown status).
    /// </summary>
    public static string? TabForItinerary(string itineraryStatus, bool hasCompletedSession)
    {
        if (hasCompletedSession)
        {
            return ListTravelerTripsQuery.TabCompleted;
        }

        return itineraryStatus switch
        {
            Itinerary.ActiveStatus => ListTravelerTripsQuery.TabUpcoming,
            Itinerary.CancelledStatus => ListTravelerTripsQuery.TabCancelled,
            _ => null,
        };
    }

    /// <summary>P5: the representative completed session's start, else ValidFrom, else CreatedAt.</summary>
    public static DateTimeOffset DepartureForItinerary(
        DateTimeOffset? completedSessionStartedAtUtc,
        DateTimeOffset? validFromUtc,
        DateTimeOffset createdAtUtc) =>
        completedSessionStartedAtUtc ?? validFromUtc ?? createdAtUtc;
}