using TripMate.Domain.Entities;

namespace TripMate.Application.Features.Traveler.Bookings.Common;

public sealed record BookedTourStopInput(
    int DayNo,
    int SequenceNo,
    long PoiId,
    TimeOnly? PlannedTime,
    int StayDurationMinutes,
    string? Notes);

public sealed record PlannedBookedTourStop(
    int SequenceNo,
    long PoiId,
    DateTimeOffset PlannedArrivalUtc,
    DateTimeOffset PlannedDepartureUtc,
    string? RecommendationReason);

public sealed record BookedTourPlanResult(
    IReadOnlyList<PlannedBookedTourStop>? Stops,
    string? FailureCode);

/// <summary>
/// Single source of truth for the BookedTour timeline (UC-70): booking preflight and
/// payment-time materialization must agree, so both call this one policy. Mapping: items
/// ordered by DayNo then SequenceNo, flattened to SequenceNo 1..N; arrival = local
/// departure date + (DayNo - 1) + PlannedTime converted from the tour time zone (default
/// Asia/Ho_Chi_Minh); departure = arrival + StayDurationMinutes; Notes become the
/// recommendation reason. Fail-closed: missing PlannedTime, empty templates, unknown time
/// zones, invalid or ambiguous DST local times, non-positive durations, overlapping stops
/// and out-of-window stops refuse with a stable failure code instead of guessing times.
/// </summary>
public static class BookedTourItineraryTimeline
{
    public const string NoStopsFailureCode = "booked_tour.no_stops";
    public const string MissingPlannedTimeFailureCode = "booked_tour.stop_missing_planned_time";
    public const string UnknownTimeZoneFailureCode = "booked_tour.unknown_timezone";
    public const string InvalidLocalTimeFailureCode = "booked_tour.invalid_local_time";
    public const string AmbiguousLocalTimeFailureCode = "booked_tour.ambiguous_local_time";
    public const string InvalidStopDurationFailureCode = "booked_tour.invalid_stop_duration";
    public const string OverlappingStopsFailureCode = "booked_tour.overlapping_stops";
    public const string StopOutsideDepartureWindowFailureCode = "booked_tour.stop_outside_departure_window";

    public static BookedTourPlanResult Plan(
        IReadOnlyList<BookedTourStopInput> stops,
        string? timeZoneId,
        DateTimeOffset scheduleStartUtc,
        DateTimeOffset scheduleEndUtc)
    {
        List<BookedTourStopInput> ordered = stops
            .OrderBy(stop => stop.DayNo)
            .ThenBy(stop => stop.SequenceNo)
            .ToList();
        if (ordered.Count == 0)
        {
            return new BookedTourPlanResult(null, NoStopsFailureCode);
        }

        if (ordered.Any(stop => !stop.PlannedTime.HasValue))
        {
            return new BookedTourPlanResult(null, MissingPlannedTimeFailureCode);
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(
                (timeZoneId ?? Tour.DefaultTimeZoneId).Trim(), out TimeZoneInfo? timeZone)
            || timeZone is null)
        {
            return new BookedTourPlanResult(null, UnknownTimeZoneFailureCode);
        }

        DateTime localDepartureDate = TimeZoneInfo.ConvertTime(scheduleStartUtc, timeZone).Date;

        var planned = new List<PlannedBookedTourStop>(ordered.Count);
        DateTimeOffset previousDepartureUtc = DateTimeOffset.MinValue;
        var sequence = 0;
        foreach (BookedTourStopInput stop in ordered)
        {
            sequence++;
            DateTime localArrival = localDepartureDate
                .AddDays(stop.DayNo - 1)
                .Add(stop.PlannedTime!.Value.ToTimeSpan());
            if (timeZone.IsInvalidTime(localArrival))
            {
                return new BookedTourPlanResult(null, InvalidLocalTimeFailureCode);
            }

            if (timeZone.IsAmbiguousTime(localArrival))
            {
                return new BookedTourPlanResult(null, AmbiguousLocalTimeFailureCode);
            }

            var arrivalUtc = TimeZoneInfo.ConvertTimeToUtc(localArrival, timeZone);
            var departureUtc = arrivalUtc.AddMinutes(stop.StayDurationMinutes);
            if (departureUtc <= arrivalUtc)
            {
                return new BookedTourPlanResult(null, InvalidStopDurationFailureCode);
            }

            if (arrivalUtc < previousDepartureUtc)
            {
                return new BookedTourPlanResult(null, OverlappingStopsFailureCode);
            }

            if (arrivalUtc < scheduleStartUtc || departureUtc > scheduleEndUtc)
            {
                return new BookedTourPlanResult(null, StopOutsideDepartureWindowFailureCode);
            }

            previousDepartureUtc = departureUtc;
            planned.Add(new PlannedBookedTourStop(
                sequence, stop.PoiId, arrivalUtc, departureUtc, stop.Notes));
        }

        return new BookedTourPlanResult(planned, null);
    }
}
