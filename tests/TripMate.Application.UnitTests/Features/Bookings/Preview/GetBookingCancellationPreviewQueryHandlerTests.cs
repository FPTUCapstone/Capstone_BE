using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Bookings.Preview;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.Preview;

public sealed class GetBookingCancellationPreviewQueryHandlerTests
{
    [Fact]
    public async Task Handle_TravelerOwnedConfirmedBooking_ReturnsServerCalculatedRefundPreview()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), 21L);
        Set(tour, nameof(Tour.OperatorUserId), 8L);
        Set(tour, nameof(Tour.Title), "Da Nang escape");
        Set(tour, nameof(Tour.BasePrice), 100m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), 31L);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), departure);
        Set(schedule, nameof(TourSchedule.EndAtUtc), departure.AddHours(8));
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.ReservedCapacity), 1);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        var booking = Booking.Restore(41, 7, schedule.Id, 1, 1_000_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, Booking.DefaultCancellationPolicySnapshot);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var handler = new GetBookingCancellationPreviewQueryHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-3) });

        var result = await handler.Handle(new GetBookingCancellationPreviewQuery(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new
        {
            BookingId = booking.Id,
            TourTitle = "Da Nang escape",
            Quantity = 1,
            TotalAmount = 1_000_000m,
            CanCancel = true,
            EstimatedRefundAmount = 700_000m,
            RefundStatus = nameof(RefundStatus.Pending),
        });
    }

    private static void Set<T>(object instance, string propertyName, T value) =>
        instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}