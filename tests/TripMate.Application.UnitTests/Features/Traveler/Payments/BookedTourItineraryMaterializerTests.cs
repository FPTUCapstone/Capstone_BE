using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Traveler.Payments;

public sealed class BookedTourItineraryMaterializerTests
{
    private static readonly DateTimeOffset DepartureStartUtc =
        new(2026, 11, 5, 3, 0, 0, TimeSpan.Zero); // 10:00 Asia/Ho_Chi_Minh

    [Fact]
    public void Materialize_MultiDayTour_MapsDayOffsetAndFlatSequence()
    {
        var tour = TourHelper(durationDays: 2,
            ("Asia/Ho_Chi_Minh", 1, 1, 501, new TimeOnly(10, 30), 60),
            ("Asia/Ho_Chi_Minh", 2, 1, 502, new TimeOnly(9, 0), 45));
        var schedule = ScheduleHelper(DepartureStartUtc, DepartureStartUtc.AddDays(2));
        var booking = BookingHelper();

        var result = BookedTourItineraryMaterializer.Materialize(tour, schedule, booking, DepartureStartUtc);

        result.FailureCode.Should().BeNull();
        var itinerary = result.Itinerary!;
        itinerary.ValidFromUtc.Should().Be(DepartureStartUtc);
        itinerary.ValidToUtc.Should().Be(DepartureStartUtc.AddDays(2));
        var items = itinerary.Items.OrderBy(item => item.SequenceNo).ToList();
        items.Should().HaveCount(2);
        // Day 1: 2026-11-05 10:30 +07 -> 03:30Z.
        items[0].SequenceNo.Should().Be(1);
        items[0].PointOfInterestId.Should().Be(501);
        items[0].PlannedArrivalUtc.Should().Be(new DateTimeOffset(2026, 11, 5, 3, 30, 0, TimeSpan.Zero));
        items[0].PlannedDepartureUtc.Should().Be(new DateTimeOffset(2026, 11, 5, 4, 30, 0, TimeSpan.Zero));
        // Day 2: 2026-11-06 09:00 +07 -> 02:00Z (offset by DayNo - 1).
        items[1].SequenceNo.Should().Be(2);
        items[1].PointOfInterestId.Should().Be(502);
        items[1].PlannedArrivalUtc.Should().Be(new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero));
        items[1].PlannedDepartureUtc.Should().Be(new DateTimeOffset(2026, 11, 6, 2, 45, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Materialize_AmbiguousLocalTime_FailsClosedWithoutGuessingOffset()
    {
        // America/New_York: 2026-11-01 01:30 local occurs twice (DST ends that morning).
        var tour = TourHelper(durationDays: 1,
            ("America/New_York", 1, 1, 501, new TimeOnly(1, 30), 60));
        var schedule = ScheduleHelper(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 1, 21, 0, 0, TimeSpan.Zero));

        var result = BookedTourItineraryMaterializer.Materialize(tour, schedule, BookingHelper(), DepartureStartUtc);

        result.Itinerary.Should().BeNull();
        result.FailureCode.Should().Be(BookedTourItineraryMaterializer.AmbiguousLocalTimeFailureCode);
    }

    [Fact]
    public void Materialize_InvalidLocalTime_FailsClosed()
    {
        // America/New_York: 2026-03-08 02:30 local does not exist (spring-forward gap).
        var tour = TourHelper(durationDays: 1,
            ("America/New_York", 1, 1, 501, new TimeOnly(2, 30), 60));
        // 07:00Z is the transition instant itself; the local departure date is March 8.
        var schedule = ScheduleHelper(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 8, 21, 0, 0, TimeSpan.Zero));

        var result = BookedTourItineraryMaterializer.Materialize(tour, schedule, BookingHelper(), DepartureStartUtc);

        result.Itinerary.Should().BeNull();
        result.FailureCode.Should().Be(BookedTourItineraryMaterializer.InvalidLocalTimeFailureCode);
    }

    [Fact]
    public void Materialize_OverlappingStops_FailsClosed()
    {
        // Second stop starts before the first one leaves (10:30+60min = 11:30 > 11:00).
        var tour = TourHelper(durationDays: 1,
            ("Asia/Ho_Chi_Minh", 1, 1, 501, new TimeOnly(10, 30), 60),
            ("Asia/Ho_Chi_Minh", 1, 2, 502, new TimeOnly(11, 0), 30));
        var schedule = ScheduleHelper(DepartureStartUtc, DepartureStartUtc.AddHours(8));

        var result = BookedTourItineraryMaterializer.Materialize(tour, schedule, BookingHelper(), DepartureStartUtc);

        result.Itinerary.Should().BeNull();
        result.FailureCode.Should().Be(BookedTourItineraryMaterializer.OverlappingStopsFailureCode);
    }

    [Fact]
    public void Materialize_StopOutsideDepartureWindow_FailsClosed()
    {
        // Day 2 stop but the departure only lasts eight hours.
        var tour = TourHelper(durationDays: 1,
            ("Asia/Ho_Chi_Minh", 2, 1, 501, new TimeOnly(10, 30), 60));
        var schedule = ScheduleHelper(DepartureStartUtc, DepartureStartUtc.AddHours(8));

        var result = BookedTourItineraryMaterializer.Materialize(tour, schedule, BookingHelper(), DepartureStartUtc);

        result.Itinerary.Should().BeNull();
        result.FailureCode.Should().Be(BookedTourItineraryMaterializer.StopOutsideDepartureWindowFailureCode);
    }

    private static Tour TourHelper(int durationDays,
        params (string TimeZoneId, int DayNo, int SequenceNo, long PoiId, TimeOnly PlannedTime, int StayMinutes)[] stops)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, 11L);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 7L);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        typeof(Tour).GetProperty(nameof(Tour.TimeZoneId))!.SetValue(tour, stops[0].TimeZoneId);
        typeof(Tour).GetProperty(nameof(Tour.DurationDays))!.SetValue(tour, durationDays);
        var itemsField = typeof(Tour).GetField("_itineraryItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<TourItineraryItem>)itemsField.GetValue(tour)!).AddRange(stops.Select(stop =>
            TourItineraryItem.Create(stop.DayNo, stop.SequenceNo, stop.PoiId, stop.PlannedTime,
                stop.StayMinutes, null)));
        return tour;
    }

    private static TourSchedule ScheduleHelper(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var schedule = TourSchedule.Create(startUtc, endUtc, "Da Nang", 20, startUtc.AddDays(-7));
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, 101L);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, 11L);
        return schedule;
    }

    private static Booking BookingHelper()
    {
        var participant = BookingParticipant.Create("Test Traveler", ParticipantType.Adult,
            null, null, null, null, null);
        var booking = Booking.Create("BK-UC70-MAT-0001", 42, 101, null, 2_500_000m, 0m, 2_500_000m,
            [participant], DepartureStartUtc.AddDays(-3), DepartureStartUtc.AddDays(-3).AddMinutes(15));
        typeof(Booking).GetProperty(nameof(Booking.Id))!.SetValue(booking, 23L);
        return booking;
    }
}