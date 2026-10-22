using System.Net;
using System.Net.Http.Json;
using System.Reflection;

using FluentAssertions;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Features.Bookings.Refunds;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Api.IntegrationTests.Bookings;

[Collection(nameof(TripMateApiFactory))]
public sealed class GetBookingRefundProgressEndpointTests
{
    [Fact]
    public async Task GetRefundProgress_TravelerOwner_ReceivesOnlyTheirRefundProgress()
    {
        await using var factory = new TripMateApiFactory();
        RefundSeed seed = await SeedRefundAsync(factory);
        using HttpClient client = factory.CreateAuthenticatedClient(seed.TravelerUserId, UserRole.Traveler);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/bookings/{seed.BookingId}/refund");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        BookingRefundProgressResponse? body = await response.Content.ReadFromJsonAsync<BookingRefundProgressResponse>();
        body.Should().NotBeNull();
        body!.BookingId.Should().Be(seed.BookingId);
        body.RefundAmount.Should().Be(500_000m);
        body.Status.Should().Be(nameof(RefundStatus.Pending));
        body.AttemptCount.Should().Be(0);
        body.LastErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task GetRefundProgress_UnrelatedTraveler_IsForbidden()
    {
        await using var factory = new TripMateApiFactory();
        RefundSeed seed = await SeedRefundAsync(factory);
        using HttpClient client = factory.CreateAuthenticatedClient(seed.TravelerUserId + 99, UserRole.Traveler);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/bookings/{seed.BookingId}/refund");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetRefundProgress_WhenBookingHasNoRefund_ReturnsNotFound()
    {
        await using var factory = new TripMateApiFactory();
        RefundSeed seed = await SeedRefundAsync(factory);
        using HttpClient client = factory.CreateAuthenticatedClient(seed.TravelerUserId, UserRole.Traveler);

        HttpResponseMessage response = await client.GetAsync($"/api/v1/bookings/{seed.BookingId + 10_000}/refund");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<RefundSeed> SeedRefundAsync(TripMateApiFactory factory) =>
        await factory.WithDbContextAsync(async db =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            User traveler = await AddUserAsync(db, UserRole.Traveler, now);
            User tourOperator = await AddUserAsync(db, UserRole.TourOperator, now);
            Tour tour = CreateTour(tourOperator.Id);
            db.Tours.Add(tour);
            await db.SaveChangesAsync();
            TourSchedule schedule = CreateSchedule(tour, now.AddDays(14));
            db.TourSchedules.Add(schedule);
            await db.SaveChangesAsync();
            Booking booking = Booking.Create(traveler.Id, schedule.Id, quantity: 1, totalAmount: 500_000m,
                BookingStatus.Cancelled, PaymentStatus.Paid);
            Set(booking, nameof(Booking.BookingCode), "BK-REFUND-0001");
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            db.Refunds.Add(Refund.CreatePending(booking.Id, transactionId: 999, cancellationOperationId: 999,
                amount: 500_000m, reason: "Traveler request", RefundInitiator.System, now));
            await db.SaveChangesAsync();
            return new RefundSeed(booking.Id, traveler.Id);
        });

    private static async Task<User> AddUserAsync(TestApiDbContext db, UserRole role, DateTimeOffset now)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid():N}@example.com",
            FullName = role.ToString(),
            Role = role,
            Status = AccountStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static Tour CreateTour(long operatorUserId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "Refund status tour");
        Set(tour, nameof(Tour.BasePrice), 500_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        return tour;
    }

    private static TourSchedule CreateSchedule(Tour tour, DateTimeOffset startAtUtc)
    {
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), startAtUtc);
        Set(schedule, nameof(TourSchedule.EndAtUtc), startAtUtc.AddHours(8));
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.ReservedCapacity), 1);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        return schedule;
    }

    private static void Set<T>(object instance, string property, T value) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(instance, value);

    private sealed record RefundSeed(long BookingId, long TravelerUserId);
}