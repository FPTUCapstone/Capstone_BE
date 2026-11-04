using TripMate.Application.Features.Traveler.Bookings.Common;
using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Traveler.Payments.Common;

/// <summary>
/// UC-70: turns the sold tour template into the traveler's immutable BookedTour itinerary
/// snapshot at payment-confirmation time. The timeline itself is computed by the shared
/// <see cref="BookedTourItineraryTimeline"/> policy — the same source of truth the booking
/// preflight uses — so a bookable tour can always be materialized. The snapshot is written
/// once and never re-read from the template, so later operator edits cannot change a paid
/// traveler's program. Every materialized stop is mandatory because it is part of the
/// contracted program.
/// </summary>
internal static class BookedTourItineraryMaterializer
{
    // Failure codes live on the shared timeline policy; aliases keep existing call sites stable.
    public const string NoStopsFailureCode = BookedTourItineraryTimeline.NoStopsFailureCode;
    public const string MissingPlannedTimeFailureCode = BookedTourItineraryTimeline.MissingPlannedTimeFailureCode;
    public const string UnknownTimeZoneFailureCode = BookedTourItineraryTimeline.UnknownTimeZoneFailureCode;
    public const string InvalidLocalTimeFailureCode = BookedTourItineraryTimeline.InvalidLocalTimeFailureCode;
    public const string AmbiguousLocalTimeFailureCode = BookedTourItineraryTimeline.AmbiguousLocalTimeFailureCode;
    public const string InvalidStopDurationFailureCode = BookedTourItineraryTimeline.InvalidStopDurationFailureCode;
    public const string OverlappingStopsFailureCode = BookedTourItineraryTimeline.OverlappingStopsFailureCode;
    public const string StopOutsideDepartureWindowFailureCode =
        BookedTourItineraryTimeline.StopOutsideDepartureWindowFailureCode;

    public sealed record MaterializationResult(Itinerary? Itinerary, string? FailureCode);

    public static MaterializationResult Materialize(
        Tour tour,
        TourSchedule schedule,
        Booking booking,
        DateTimeOffset confirmedAtUtc)
    {
        BookedTourPlanResult plan = BookedTourItineraryTimeline.Plan(
            tour.ItineraryItems
                .Select(stop => new BookedTourStopInput(
                    stop.DayNo, stop.SequenceNo, stop.PoiId,
                    stop.PlannedTime, stop.StayDurationMinutes, stop.Notes))
                .ToList(),
            tour.TimeZoneId,
            schedule.StartAtUtc,
            schedule.EndAtUtc);
        if (plan.Stops is null)
        {
            return new MaterializationResult(null, plan.FailureCode);
        }

        var itinerary = Itinerary.CreateBookedTour(
            booking.TravelerUserId, booking.BookingCode, booking.Id, tour.Id, confirmedAtUtc,
            validFromUtc: schedule.StartAtUtc, validToUtc: schedule.EndAtUtc);
        foreach (PlannedBookedTourStop stop in plan.Stops)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                stop.SequenceNo, stop.PoiId, stop.PlannedArrivalUtc, stop.PlannedDepartureUtc,
                isMandatory: true, estimatedCost: null, recommendationReason: stop.RecommendationReason));
        }

        return new MaterializationResult(itinerary, null);
    }
}
