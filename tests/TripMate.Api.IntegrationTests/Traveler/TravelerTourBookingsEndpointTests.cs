using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Traveler;

/// <summary>
/// UC-27 Book Tour. Exercises the real SQL Server-backed create endpoint end to end:
/// idempotent replay and the capacity race that SERIALIZABLE + the booking_code/idempotency
/// unique indexes must resolve to exactly one winner.
/// </summary>
[Collection(nameof(TripMateApiFactory))]
public sealed class TravelerTourBookingsEndpointTests
{
    private const string Endpoint = "/api/v1/traveler/tour-bookings";

    [Fact]
    public async Task Create_WithSameIdempotencyKeyTwice_ReplaysTheOriginalBookingWithoutDoubleReservingCapacity()
    {
        using var factory = new TripMateApiFactory();
        var (travelerId, scheduleId) = await SeedTravelerAndScheduleAsync(factory, 91, totalCapacity: 5);
        using var client = factory.CreateAuthenticatedClient(travelerId, UserRole.Traveler);
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var request = ValidRequest(scheduleId);

        using var first = await client.PostAsJsonAsync(Endpoint, request);
        using var second = await client.PostAsJsonAsync(Endpoint, request);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadBookingId(await first.Content.ReadAsStringAsync())
            .Should().Be(ReadBookingId(await second.Content.ReadAsStringAsync()));

        await factory.WithDbContextAsync(async db =>
        {
            (await db.Bookings.CountAsync()).Should().Be(1);
            (await db.TourSchedules.AsNoTracking().SingleAsync(s => s.Id == scheduleId)).ReservedCapacity
                .Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task Create_WithSameIdempotencyKeyAndDifferentPayload_ReturnsConflict()
    {
        using var factory = new TripMateApiFactory();
        var (travelerId, scheduleId) = await SeedTravelerAndScheduleAsync(factory, 92, totalCapacity: 5);
        using var client = factory.CreateAuthenticatedClient(travelerId, UserRole.Traveler);
        var key = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var first = await client.PostAsJsonAsync(Endpoint, ValidRequest(scheduleId, "Nguyen Van A"));
        using var second = await client.PostAsJsonAsync(Endpoint, ValidRequest(scheduleId, "Different Name"));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await factory.WithDbContextAsync(db => db.Bookings.CountAsync())).Should().Be(1);
    }

    private static long ReadBookingId(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        return long.Parse(json.RootElement.GetProperty("bookingId").GetString()!);
    }

    private static object ValidRequest(long scheduleId, string fullName = "Nguyen Van A") => new
    {
        tourScheduleId = scheduleId.ToString(),
        participants = new[] { new { fullName, participantType = "Adult" } },
    };

    private static Task<(long TravelerId, long ScheduleId)> SeedTravelerAndScheduleAsync(
        TripMateApiFactory factory, long travelerId, int totalCapacity) =>
        factory.WithDbContextAsync(async db =>
        {
            var traveler = new User
            {
                Id = travelerId,
                Email = $"traveler-{travelerId}@test.local",
                FullName = "Traveler",
                Role = UserRole.Traveler,
                Status = AccountStatus.Active,
            };
            db.Users.Add(traveler);

            var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
            Set(tour, nameof(Tour.OperatorUserId), 1L);
            Set(tour, nameof(Tour.Title), "Integration booking tour");
            Set(tour, nameof(Tour.BasePrice), 1_000_000m);
            Set(tour, nameof(Tour.DurationDays), 1);
            Set(tour, nameof(Tour.Status), TourStatus.Published);
            // UC-70 bookability: a sellable tour template carries fully scheduled stops.
            var itemsField = typeof(Tour).GetField("_itineraryItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((List<TourItineraryItem>)itemsField.GetValue(tour)!).AddRange(
            [
                TourItineraryItem.Create(1, 1, 9001, new TimeOnly(9, 0), 60, null),
                TourItineraryItem.Create(1, 2, 9002, new TimeOnly(11, 0), 45, null),
            ]);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();

            var schedule = TourSchedule.Create(
                // Schedule opens before the first stop (09:00 +07 = 02:00Z) per the UC-70 timeline.
                DateTimeOffset.UtcNow.Date.AddDays(7).AddHours(1), DateTimeOffset.UtcNow.Date.AddDays(8).AddHours(1), null, totalCapacity,
                DateTimeOffset.UtcNow, adultPrice: 1_000_000m);
            Set(schedule, nameof(TourSchedule.TourId), tour.Id);
            Set(schedule, nameof(TourSchedule.Tour), tour);
            db.TourSchedules.Add(schedule);
            await db.SaveChangesAsync();
            return (travelerId, schedule.Id);
        });

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}