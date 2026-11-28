using FluentAssertions;

using TripMate.Application.Features.Weather.PublicForecast;
using TripMate.Application.Features.Weather.UpcomingTrips;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Weather.UpcomingTrips;

public sealed class GetUpcomingTripWeatherQueryHandlerTests
{
    // 26/11/2026 10:00 local (UTC+7).
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 3, 0, 0, TimeSpan.Zero);
    private const long Traveler = 10;

    [Fact]
    public async Task Handle_ListsConfirmedTripsWithinFiveDaysOrInProgress_NearestFirst_AtMostThree()
    {
        await using var db = TestDbContext.Create();
        var tour = SeedTour(db, 1, "Tour Bà Nà");
        SeedTrip(db, tour, 101, Now.AddDays(-1), Now.AddDays(1)); // in progress
        SeedTrip(db, tour, 102, Now.AddDays(2), Now.AddDays(3));
        SeedTrip(db, tour, 103, Now.AddDays(1), Now.AddDays(2));
        SeedTrip(db, tour, 104, Now.AddDays(4), Now.AddDays(5)); // 4th nearest: cut by the cap
        SeedTrip(db, tour, 105, Now.AddDays(6), Now.AddDays(7)); // beyond 5 days
        SeedTrip(db, tour, 106, Now.AddDays(-3), Now.AddHours(-1)); // already ended
        SeedTrip(db, tour, 107, Now.AddDays(1), Now.AddDays(2), status: BookingStatus.PendingPayment);
        SeedTrip(db, tour, 108, Now.AddDays(1), Now.AddDays(2), status: BookingStatus.Cancelled);
        SeedTrip(db, tour, 109, Now.AddDays(1), Now.AddDays(2), traveler: 99);
        await db.SaveChangesAsync();

        var result = await Handler(db, new FakeProvider()).Handle(new GetUpcomingTripWeatherQuery(Traveler),
            CancellationToken.None);

        result.Value.Trips.Select(trip => trip.BookingId).Should().Equal(101, 103, 102);
        result.Value.Trips[0].InProgress.Should().BeTrue();
        result.Value.Trips[1].InProgress.Should().BeFalse();
        result.Value.Trips[1].TourTitle.Should().Be("Tour Bà Nà");
    }

    [Fact]
    public async Task Handle_UsesMeetingPointForecast_ForTheDepartureLocalDate()
    {
        await using var db = TestDbContext.Create();
        var tour = SeedTour(db, 1, "Tour Bà Nà");
        // 27/11 23:30 UTC is 28/11 06:30 in Vietnam.
        var start = new DateTimeOffset(2026, 11, 27, 23, 30, 0, TimeSpan.Zero);
        SeedTrip(db, tour, 101, start, start.AddHours(10), meeting: (16.0544m, 108.2022m));
        await db.SaveChangesAsync();
        var provider = new FakeProvider(Day("2026-11-27"), Day("2026-11-28"));

        var trip = (await Handler(db, provider).Handle(new GetUpcomingTripWeatherQuery(Traveler),
            CancellationToken.None)).Value.Trips.Single();

        provider.Requested.Should().Equal((16.05m, 108.20m));
        trip.Point.Should().Be(new UpcomingTripPointDto("Khách sạn Đà Nẵng", 16.0544m, 108.2022m));
        trip.ForecastDate.Should().Be("2026-11-28");
        trip.Forecast!.Date.Should().Be("2026-11-28");
    }

    [Fact]
    public async Task Handle_TripInProgress_ShowsTodaysForecast()
    {
        await using var db = TestDbContext.Create();
        var tour = SeedTour(db, 1, "Tour Huế");
        SeedTrip(db, tour, 101, Now.AddDays(-2), Now.AddDays(1), meeting: (16.46m, 107.59m));
        await db.SaveChangesAsync();

        var trip = (await Handler(db, new FakeProvider(Day("2026-11-24"), Day("2026-11-26")))
            .Handle(new GetUpcomingTripWeatherQuery(Traveler), CancellationToken.None)).Value.Trips.Single();

        trip.ForecastDate.Should().Be("2026-11-26");
        trip.Forecast!.Date.Should().Be("2026-11-26");
    }

    [Fact]
    public async Task Handle_WithoutMeetingCoordinates_FallsBackToTheFirstItineraryStop()
    {
        await using var db = TestDbContext.Create();
        var tour = SeedTour(db, 1, "Tour Hội An");
        SeedStop(db, tour, dayNo: 1, sequenceNo: 2, "Chùa Cầu", 15.8775m, 108.3260m);
        SeedStop(db, tour, dayNo: 1, sequenceNo: 1, "Phố cổ Hội An", 15.8770m, 108.3280m);
        SeedTrip(db, tour, 101, Now.AddDays(1), Now.AddDays(2), meeting: null);
        await db.SaveChangesAsync();

        var trip = (await Handler(db, new FakeProvider()).Handle(new GetUpcomingTripWeatherQuery(Traveler),
            CancellationToken.None)).Value.Trips.Single();

        trip.Point.Should().Be(new UpcomingTripPointDto("Phố cổ Hội An", 15.8770m, 108.3280m));
    }

    [Fact]
    public async Task Handle_WithoutAnyPoint_StillListsTheTripWithoutForecast()
    {
        await using var db = TestDbContext.Create();
        var tour = SeedTour(db, 1, "Tour bí ẩn");
        SeedTrip(db, tour, 101, Now.AddDays(1), Now.AddDays(2), meeting: null);
        await db.SaveChangesAsync();
        var provider = new FakeProvider(Day("2026-11-27"));

        var trip = (await Handler(db, provider).Handle(new GetUpcomingTripWeatherQuery(Traveler),
            CancellationToken.None)).Value.Trips.Single();

        trip.Point.Should().BeNull();
        trip.Forecast.Should().BeNull();
        provider.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenForecastIsUnavailable_StillListsTheTrip()
    {
        await using var db = TestDbContext.Create();
        var tour = SeedTour(db, 1, "Tour Bà Nà");
        SeedTrip(db, tour, 101, Now.AddDays(1), Now.AddDays(2), meeting: (16.05m, 108.2m));
        await db.SaveChangesAsync();

        var trip = (await Handler(db, new FakeProvider(unavailable: true))
            .Handle(new GetUpcomingTripWeatherQuery(Traveler), CancellationToken.None)).Value.Trips.Single();

        trip.Point.Should().NotBeNull();
        trip.Forecast.Should().BeNull();
    }

    [Fact]
    public async Task Handle_OpenSafetyReview_BecomesTheWarning_WithTheEventAndOperatorInstructions()
    {
        await using var db = TestDbContext.Create();
        var tour = SeedTour(db, 1, "Tour Bà Nà");
        var schedule = SeedTrip(db, tour, 101, Now.AddDays(1), Now.AddDays(2), meeting: (16.05m, 108.2m));
        db.WeatherEvents.Add(WeatherEvent.CreateOfficial("tomorrow", "evt-1", "Thunderstorm", WeatherEvent.SeveritySevere,
            Now.AddDays(1), Now.AddDays(1).AddHours(3), "Dông lớn chiều nay", null, null, null, Now));
        var review = WeatherSafetyReview.CreateHold(schedule.Id, "tomorrow", "evt-1", Now, Now.AddMinutes(30));
        review.Acknowledge(operatorId: 2, Now.AddMinutes(5), "Mang áo mưa, tập trung sớm 30 phút");
        db.WeatherSafetyReviews.Add(review);
        await db.SaveChangesAsync();

        var trip = (await Handler(db, new FakeProvider()).Handle(new GetUpcomingTripWeatherQuery(Traveler),
            CancellationToken.None)).Value.Trips.Single();

        trip.Warning.Should().Be(new UpcomingTripWarningDto(WeatherSafetyReview.StatusAcknowledged, "Thunderstorm",
            WeatherEvent.SeveritySevere, "Dông lớn chiều nay", Now.AddDays(1), Now.AddDays(1).AddHours(3),
            "Mang áo mưa, tập trung sớm 30 phút"));
    }

    [Fact]
    public async Task Handle_ClosedSafetyReview_ShowsNoWarning()
    {
        await using var db = TestDbContext.Create();
        var tour = SeedTour(db, 1, "Tour Bà Nà");
        var schedule = SeedTrip(db, tour, 101, Now.AddDays(1), Now.AddDays(2), meeting: (16.05m, 108.2m));
        var review = WeatherSafetyReview.CreateHold(schedule.Id, "tomorrow", "evt-1", Now, Now.AddMinutes(30));
        review.Close();
        db.WeatherSafetyReviews.Add(review);
        await db.SaveChangesAsync();

        var trip = (await Handler(db, new FakeProvider()).Handle(new GetUpcomingTripWeatherQuery(Traveler),
            CancellationToken.None)).Value.Trips.Single();

        trip.Warning.Should().BeNull();
    }

    private static GetUpcomingTripWeatherQueryHandler Handler(TestDbContext db, FakeProvider provider) =>
        new(db, provider, new FakeDateTimeProvider { UtcNow = Now });

    private static Tour SeedTour(TestDbContext db, long id, string title)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.Id))!.SetValue(tour, id);
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, 2L);
        typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, title);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        db.Tours.Add(tour);
        return tour;
    }

    private static TourSchedule SeedTrip(TestDbContext db, Tour tour, long bookingId, DateTimeOffset start,
        DateTimeOffset end, BookingStatus status = BookingStatus.Confirmed, long traveler = Traveler,
        (decimal Latitude, decimal Longitude)? meeting = default)
    {
        var schedule = TourSchedule.Create(start, end, "Khách sạn Đà Nẵng", 20, Now.AddDays(-30));
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Id))!.SetValue(schedule, bookingId + 1000);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        if (meeting is { } point)
        {
            schedule.SetVerifiedMeetingCoordinates(point.Latitude, point.Longitude);
        }

        db.TourSchedules.Add(schedule);
        var booking = Booking.Restore(bookingId, traveler, schedule.Id, 1, 100_000m, status, PaymentStatus.Paid,
            Booking.DefaultCancellationPolicySnapshot);
        typeof(Booking).GetProperty(nameof(Booking.TourSchedule))!.SetValue(booking, schedule);
        typeof(Booking).GetProperty(nameof(Booking.BookingCode))!.SetValue(booking, $"TM{bookingId}");
        db.Bookings.Add(booking);
        return schedule;
    }

    private static void SeedStop(TestDbContext db, Tour tour, int dayNo, int sequenceNo, string name,
        decimal latitude, decimal longitude)
    {
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        var poiId = 500L + (dayNo * 10) + sequenceNo;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Id))!.SetValue(poi, poiId);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Name))!.SetValue(poi, name);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Latitude))!.SetValue(poi, latitude);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Longitude))!.SetValue(poi, longitude);
        db.PointsOfInterest.Add(poi);
        var item = TourItineraryItem.Create(dayNo, sequenceNo, poiId, null, 60, null);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Id))!.SetValue(item, poiId);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.TourId))!.SetValue(item, tour.Id);
        typeof(TourItineraryItem).GetProperty(nameof(TourItineraryItem.Poi))!.SetValue(item, poi);
        db.TourItineraryItems.Add(item);
    }

    private static PublicWeatherDayDto Day(string date) =>
        new(date, 23, 31, 33, 35, 0.4, 1.2, 8, 6.2, false, 1101, 74, "05:56", "17:18");

    private sealed class FakeProvider(params PublicWeatherDayDto[] days) : IPublicWeatherForecastProvider
    {
        private readonly bool _unavailable;

        public FakeProvider(bool unavailable) : this() => _unavailable = unavailable;

        public List<(decimal Latitude, decimal Longitude)> Requested { get; } = [];

        public Task<PublicWeatherForecastData?> GetAsync(decimal latitude, decimal longitude,
            CancellationToken cancellationToken)
        {
            Requested.Add((latitude, longitude));
            return Task.FromResult(_unavailable ? null : new PublicWeatherForecastData(Now, [], days));
        }
    }
}