using System.Reflection;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Weather.PublicForecast;
using TripMate.Application.Features.Weather.UpcomingTrips;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Services;

namespace TripMate.Api.IntegrationTests.Weather;

/// <summary>Upcoming Trip Weather queries translate and join correctly on real SQL Server.</summary>
public sealed class UpcomingTripWeatherSqlServerTests
{
    // 10:00 Asia/Ho_Chi_Minh on 26/11; the departure starts 28/11 06:30 local.
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DepartureStartUtc = new(2026, 11, 27, 23, 30, 0, TimeSpan.Zero);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ConfirmedBooking_ReturnsFirstStopForecast_AndTheOpenSafetyReview()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var (travelerId, bookingId) = await SeedAsync(database);
        await using var db = database.CreateDbContext();
        var provider = new StubProvider();

        var result = await new GetUpcomingTripWeatherQueryHandler(db, provider, new FixedClock(Now))
            .Handle(new GetUpcomingTripWeatherQuery(travelerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("{0}", result.ErrorMessage);
        var trip = result.Value.Trips.Should().ContainSingle().Subject;
        trip.BookingId.Should().Be(bookingId);
        trip.TourTitle.Should().Be("Tour Bà Nà");
        trip.Point.Should().Be(new UpcomingTripPointDto("Cầu Vàng", 15.995m, 107.996m));
        trip.ForecastDate.Should().Be("2026-11-28");
        trip.Forecast!.Date.Should().Be("2026-11-28");
        provider.Requested.Should().Equal((16.00m, 108.00m));
        trip.Warning.Should().Be(new UpcomingTripWarningDto(WeatherSafetyReview.StatusPendingAcknowledgement,
            "Thunderstorm", WeatherEvent.SeveritySevere, "Dông lớn", DepartureStartUtc, DepartureStartUtc.AddHours(4),
            null));
    }

    private static async Task<(long TravelerId, long BookingId)> SeedAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateDbContext();
        var operatorUser = new User
        {
            Email = "weather-operator@example.test",
            FullName = "Weather Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
            PasswordHash = new PasswordHasherService().Hash("OperatorPass123!"),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        var traveler = new User
        {
            Email = "weather-traveler@example.test",
            FullName = "Weather Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        db.Users.AddRange(operatorUser, traveler);
        var category = PoiCategory.Create("Mountain", null);
        db.PoiCategories.Add(category);
        await db.SaveChangesAsync();

        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            User = operatorUser,
            CompanyName = "Weather Tours",
            TaxCode = "WEATHER-TAX",
            BusinessLicenseNo = "WEATHER-LICENSE",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });
        var poi = (PointOfInterest)Activator.CreateInstance(typeof(PointOfInterest), nonPublic: true)!;
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Name))!.SetValue(poi, "Cầu Vàng");
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Latitude))!.SetValue(poi, 15.995m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.Longitude))!.SetValue(poi, 107.996m);
        typeof(PointOfInterest).GetProperty(nameof(PointOfInterest.CategoryId))!.SetValue(poi, category.Id);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();

        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, operatorUser.Id);
        typeof(Tour).GetProperty(nameof(Tour.Status))!.SetValue(tour, TourStatus.Published);
        typeof(Tour).GetProperty(nameof(Tour.TimeZoneId))!.SetValue(tour, "Asia/Ho_Chi_Minh");
        typeof(Tour).GetProperty(nameof(Tour.DurationDays))!.SetValue(tour, 1);
        typeof(Tour).GetProperty(nameof(Tour.Title))!.SetValue(tour, "Tour Bà Nà");
        typeof(Tour).GetProperty(nameof(Tour.BasePrice))!.SetValue(tour, 1_000_000m);
        var itemsField = typeof(Tour).GetField("_itineraryItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<TourItineraryItem>)itemsField.GetValue(tour)!).Add(
            TourItineraryItem.Create(1, 1, poi.Id, new TimeOnly(9, 0), 60, null));
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        // No meeting coordinates: the card falls back to the first itinerary stop.
        var schedule = TourSchedule.Create(DepartureStartUtc, DepartureStartUtc.AddHours(10), "Đà Nẵng", 20, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.Tour))!.SetValue(schedule, tour);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var participant = BookingParticipant.Create("Weather Traveler", ParticipantType.Adult,
            null, null, null, null, null);
        var booking = Booking.Create("BK-WEATHER-0001", traveler.Id, schedule.Id, null,
            1_000_000m, 0m, 1_000_000m, [participant], Now.AddMinutes(-5), Now.AddMinutes(15));
        typeof(Booking).GetProperty(nameof(Booking.Status))!.SetValue(booking, BookingStatus.Confirmed);
        db.Bookings.Add(booking);
        db.WeatherEvents.Add(WeatherEvent.CreateOfficial("tomorrow", "evt-banana", "Thunderstorm",
            WeatherEvent.SeveritySevere, DepartureStartUtc, DepartureStartUtc.AddHours(4), "Dông lớn",
            null, null, null, Now));
        await db.SaveChangesAsync();

        var closed = WeatherSafetyReview.CreateHold(schedule.Id, "tomorrow", "evt-old", Now.AddHours(-5),
            Now.AddHours(-4));
        closed.Close();
        db.WeatherSafetyReviews.AddRange(closed,
            WeatherSafetyReview.CreateHold(schedule.Id, "tomorrow", "evt-banana", Now, Now.AddMinutes(30)));
        await db.SaveChangesAsync();
        return (traveler.Id, booking.Id);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class StubProvider : IPublicWeatherForecastProvider
    {
        public List<(decimal Latitude, decimal Longitude)> Requested { get; } = [];

        public Task<PublicWeatherForecastData?> GetAsync(decimal latitude, decimal longitude,
            CancellationToken cancellationToken)
        {
            Requested.Add((latitude, longitude));
            PublicWeatherDayDto[] days =
            [
                new("2026-11-27", 22, 30, 32, 20, 0.1, 0.2, 7, 5, false, 1100, 70, "05:55", "17:18"),
                new("2026-11-28", 21, 28, 30, 80, 3.5, 12, 6, 9, true, 8000, 85, "05:56", "17:18"),
            ];
            return Task.FromResult<PublicWeatherForecastData?>(new PublicWeatherForecastData(Now, [], days));
        }
    }
}