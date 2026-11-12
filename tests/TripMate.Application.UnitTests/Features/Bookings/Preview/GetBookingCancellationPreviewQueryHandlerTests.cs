using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Bookings.Common;
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
        // Paid well before "now" (and more than 24h ahead of it), so the 24h-since-payment
        // full-refund rule never interferes with the BR-135 v3 tier being tested.
        db.PaymentTransactions.Add(CreateSuccessfulPayment(51, booking.Id, booking.TotalAmount, departure.AddDays(-25)));
        await db.SaveChangesAsync();

        var handler = new GetBookingCancellationPreviewQueryHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-12) });

        var result = await handler.Handle(new GetBookingCancellationPreviewQuery(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new
        {
            BookingId = booking.Id,
            TourTitle = "Da Nang escape",
            Quantity = 1,
            TotalAmount = 1_000_000m,
            CanCancel = true,
            EstimatedRefundAmount = 500_000m, // BR-135 v3: 10-14 days before departure = 50%.
            RefundStatus = nameof(RefundStatus.Pending),
        });
    }

    [Fact]
    public async Task Handle_ConfirmedBookingWithoutMatchingSuccessfulPayment_ReturnsSameRefundFailureAsCancellation()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure);
        var booking = Booking.Restore(41, 7, schedule.Id, 1, 1_000_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, Booking.DefaultCancellationPolicySnapshot);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var handler = new GetBookingCancellationPreviewQueryHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddDays(-20) }); // BR-135 v3: >14 days = 100%, so a non-zero refund is actually at stake here.

        var result = await handler.Handle(new GetBookingCancellationPreviewQuery(booking.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new
        {
            CanCancel = false,
            BlockingReasonCode = BookingCancellationErrorCodes.PaymentNotRefundable,
            EstimatedRefundAmount = 0m,
            RefundStatus = "NotRequired",
        });
    }

    [Fact]
    public async Task Handle_TravelerInsideSeventyTwoHours_PreviewsZeroRefundAndRequiresConfirmation()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var booking = await SeedConfirmedBookingAsync(db, departure, paidAtUtc: departure.AddDays(-10));

        var handler = new GetBookingCancellationPreviewQueryHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = departure.AddHours(-24) });

        var result = await handler.Handle(new GetBookingCancellationPreviewQuery(booking.Id), CancellationToken.None);

        result.Value.Should().BeEquivalentTo(new
        {
            CanCancel = true,
            BlockingReasonCode = BookingCancellationErrorCodes.ConfirmationRequired,
            EstimatedRefundAmount = 0m,
        }, options => options.ExcludingMissingMembers());
    }

    [Fact]
    public async Task Handle_TravelerWithinTwentyFourHoursOfPaymentPreviewsFullRefund()
    {
        await using var db = TestDbContext.Create();
        var departure = new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        var now = departure.AddHours(-24);
        var booking = await SeedConfirmedBookingAsync(db, departure, paidAtUtc: now.AddHours(-2));

        var handler = new GetBookingCancellationPreviewQueryHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.Traveler) },
            new FakeDateTimeProvider { UtcNow = now });

        var result = await handler.Handle(new GetBookingCancellationPreviewQuery(booking.Id), CancellationToken.None);

        result.Value.Should().BeEquivalentTo(new
        {
            CanCancel = true,
            EstimatedRefundAmount = 1_000_000m,
        }, options => options.ExcludingMissingMembers());
    }

    private static async Task<Booking> SeedConfirmedBookingAsync(TestDbContext db, DateTimeOffset departure,
        DateTimeOffset paidAtUtc)
    {
        var tour = CreateTour(21, 8);
        var schedule = CreateSchedule(31, tour, departure);
        var booking = Booking.Restore(41, 7, schedule.Id, 1, 1_000_000m, BookingStatus.Confirmed,
            PaymentStatus.Paid, Booking.DefaultCancellationPolicySnapshot);
        db.Tours.Add(tour);
        db.TourSchedules.Add(schedule);
        db.Bookings.Add(booking);
        db.PaymentTransactions.Add(CreateSuccessfulPayment(51, booking.Id, booking.TotalAmount, paidAtUtc));
        await db.SaveChangesAsync();
        return booking;
    }

    private static Tour CreateTour(long id, long operatorUserId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), id);
        Set(tour, nameof(Tour.OperatorUserId), operatorUserId);
        Set(tour, nameof(Tour.Title), "Da Nang escape");
        Set(tour, nameof(Tour.BasePrice), 100m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Approved);
        return tour;
    }

    private static TourSchedule CreateSchedule(long id, Tour tour, DateTimeOffset departure)
    {
        var schedule = (TourSchedule)Activator.CreateInstance(typeof(TourSchedule), nonPublic: true)!;
        Set(schedule, nameof(TourSchedule.Id), id);
        Set(schedule, nameof(TourSchedule.TourId), tour.Id);
        Set(schedule, nameof(TourSchedule.Tour), tour);
        Set(schedule, nameof(TourSchedule.StartAtUtc), departure);
        Set(schedule, nameof(TourSchedule.EndAtUtc), departure.AddHours(8));
        Set(schedule, nameof(TourSchedule.TotalCapacity), 10);
        Set(schedule, nameof(TourSchedule.ReservedCapacity), 1);
        Set(schedule, nameof(TourSchedule.Status), TourScheduleStatus.Scheduled);
        return schedule;
    }

    private static PaymentTransaction CreateSuccessfulPayment(long id, long bookingId, decimal amount,
        DateTimeOffset createdAtUtc)
    {
        var transaction = (PaymentTransaction)Activator.CreateInstance(typeof(PaymentTransaction), nonPublic: true)!;
        Set(transaction, nameof(PaymentTransaction.Id), id);
        Set(transaction, nameof(PaymentTransaction.BookingId), bookingId);
        Set(transaction, nameof(PaymentTransaction.Amount), amount);
        Set(transaction, nameof(PaymentTransaction.TransactionType), PaymentTransaction.TypePayment);
        Set(transaction, nameof(PaymentTransaction.Status), PaymentTransactionStatus.Success);
        Set(transaction, nameof(PaymentTransaction.CreatedAtUtc), createdAtUtc);
        Set(transaction, nameof(PaymentTransaction.SucceededAtUtc), createdAtUtc);
        return transaction;
    }

    private static void Set<T>(object instance, string propertyName, T value) =>
        instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}