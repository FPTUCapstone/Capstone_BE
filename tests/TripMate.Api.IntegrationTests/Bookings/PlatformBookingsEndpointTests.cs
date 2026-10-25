using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Bookings;

[Collection(nameof(TripMateApiFactory))]
public sealed class PlatformBookingsEndpointTests
{
    private const string Route = "/api/v1/admin/bookings";

    [Fact]
    public async Task Get_Anonymous_ReturnsUnauthorized()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Administrator_ReturnsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(2, UserRole.Administrator);

        HttpResponseMessage response = await client.GetAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_StaffWithNoBookings_ReturnsEmptyPage()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        HttpResponseMessage response = await client.GetAsync(Route);
        PlatformBookingsContract? body =
            await response.Content.ReadFromJsonAsync<PlatformBookingsContract>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.PageNumber.Should().Be(1);
        body.PageSize.Should().Be(20);
        body.TotalCount.Should().Be(0);
        body.TotalPages.Should().Be(0);
        body.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_InvertedBookedRange_ReturnsStableBadRequest()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        HttpResponseMessage response = await client.GetAsync(
            Route + "?bookedFromUtc=2026-10-02T00:00:00Z&bookedToUtc=2026-10-01T00:00:00Z");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("platform_booking.invalid_date_range");
    }

    [Fact]
    public async Task Get_StaffFiltersInSqlAndReturnsOnlyOperationalBookingFields()
    {
        await using var factory = new TripMateApiFactory();
        BookingSeed seed = await SeedBookingsAsync(factory);
        using var client = factory.CreateAuthenticatedClient(1, UserRole.Staff);

        HttpResponseMessage response = await client.GetAsync(
            Route + "?keyword=lantern&status=Confirmed&paymentStatus=Paid&pageSize=1");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement root = body.RootElement;
        root.GetProperty("totalCount").GetInt32().Should().Be(1);
        root.GetProperty("totalPages").GetInt32().Should().Be(1);
        JsonElement item = root.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("bookingId").GetString().Should().Be(seed.ConfirmedBookingId.ToString());
        item.GetProperty("bookingCode").GetString().Should().Be("BK-PLATFORM-0001");
        item.GetProperty("traveler").GetProperty("email").GetString().Should().Be("traveler@example.com");
        item.GetProperty("tour").GetProperty("title").GetString().Should().Be("Lantern Festival Tour");
        item.TryGetProperty("paymentTransactions", out _).Should().BeFalse();
        item.TryGetProperty("gateway", out _).Should().BeFalse();
    }

    private static Task<BookingSeed> SeedBookingsAsync(TripMateApiFactory factory) =>
        factory.WithDbContextAsync(async db =>
        {
            DateTimeOffset now = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
            var traveler = new User
            {
                Email = "traveler@example.com",
                FullName = "Platform Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            var tourOperator = new User
            {
                Email = "operator@example.com",
                FullName = "Lantern Operator",
                Role = UserRole.TourOperator,
                Status = AccountStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.Users.AddRange(traveler, tourOperator);
            await db.SaveChangesAsync();

            var profile = new OperatorProfile
            {
                UserId = tourOperator.Id,
                User = tourOperator,
                CompanyName = "Lantern Co.",
                TaxCode = "TAX-UC62",
                BusinessLicenseNo = "LIC-UC62",
                ApprovalStatus = OperatorApprovalStatus.Approved,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.OperatorProfiles.Add(profile);
            await db.SaveChangesAsync();

            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), tourOperator.Id);
            Set(tour, nameof(Tour.OperatorProfile), profile);
            Set(tour, nameof(Tour.Title), "Lantern Festival Tour");
            Set(tour, nameof(Tour.BasePrice), 500_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), TourStatus.Published);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();

            var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
            Set(schedule, nameof(TourSchedule.TourId), tour.Id);
            Set(schedule, nameof(TourSchedule.Tour), tour);
            Set(schedule, nameof(TourSchedule.StartAtUtc), now.AddDays(14));
            Set(schedule, nameof(TourSchedule.EndAtUtc), now.AddDays(14).AddHours(8));
            Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
            Set(schedule, nameof(TourSchedule.ReservedCapacity), 2);
            Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
            db.TourSchedules.Add(schedule);
            await db.SaveChangesAsync();

            Booking confirmed = Booking.Create(traveler.Id, schedule.Id, 1, 500_000m,
                BookingStatus.Confirmed, PaymentStatus.Paid);
            Set(confirmed, nameof(Booking.BookingCode), "BK-PLATFORM-0001");
            Set(confirmed, nameof(Booking.BookedAtUtc), now);
            Booking cancelled = Booking.Create(traveler.Id, schedule.Id, 1, 500_000m,
                BookingStatus.Cancelled, PaymentStatus.Refunded);
            Set(cancelled, nameof(Booking.BookingCode), "BK-PLATFORM-0002");
            Set(cancelled, nameof(Booking.BookedAtUtc), now.AddMinutes(-1));
            db.Bookings.AddRange(confirmed, cancelled);
            await db.SaveChangesAsync();

            return new BookingSeed(confirmed.Id);
        });

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed record PlatformBookingsContract(
        int PageNumber,
        int PageSize,
        int TotalCount,
        int TotalPages,
        IReadOnlyList<object> Items);

    private sealed record BookingSeed(long ConfirmedBookingId);
}