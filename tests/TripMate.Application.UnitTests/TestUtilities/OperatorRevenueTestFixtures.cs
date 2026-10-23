using System.Reflection;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.TestUtilities;

/// <summary>Shared seeding helpers for UC-44/UC-45 operator revenue tests (GetReport and Exports).</summary>
public static class OperatorRevenueTestFixtures
{
    public static async Task<User> SeedApprovedOperatorAsync(
        TestDbContext db, long id, decimal commissionRate = 10m)
    {
        var user = new User
        {
            Id = id,
            Email = $"operator{id}@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            User = user,
            CompanyName = "Company",
            TaxCode = "Tax",
            BusinessLicenseNo = "License",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CommissionRate = commissionRate,
        });
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Booking> SeedPaidBookingAsync(
        TestDbContext db, long bookingId, long scheduleId, decimal totalAmount, DateTimeOffset succeededAtUtc)
    {
        var travelerId = 900 + bookingId;
        db.Users.Add(new User
        {
            Id = travelerId,
            Email = $"traveler{travelerId}@test.local",
            FullName = "Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        });

        var bookedAt = succeededAtUtc.AddMinutes(-60);
        var paymentExpiresAtUtc = succeededAtUtc.AddMinutes(30);
        var participant = BookingParticipant.Create("Adult", ParticipantType.Adult, null, null, null, null, null);
        var booking = Booking.Create(
            bookingCode: $"BK-TEST-{bookingId}",
            travelerUserId: travelerId,
            tourScheduleId: scheduleId,
            voucherId: null,
            unitPrice: totalAmount,
            discountAmount: 0m,
            totalAmount: totalAmount,
            participants: [participant],
            bookedAtUtc: bookedAt,
            paymentExpiresAtUtc: paymentExpiresAtUtc);
        Set(booking, nameof(Booking.Id), bookingId);
        booking.MarkPaid(succeededAtUtc);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var transaction = PaymentTransaction.Create(
            bookingId: booking.Id,
            gateway: PaymentGateway.Mock,
            amount: totalAmount,
            orderCode: bookingId,
            createdAtUtc: bookedAt);
        transaction.MarkSuccess(gatewayRef: null, rawResponse: null, nowUtc: succeededAtUtc, commissionRateApplied: 10m);
        db.PaymentTransactions.Add(transaction);
        await db.SaveChangesAsync();

        return booking;
    }

    public static Tour CreateTour(long id, long operatorId, string title)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), id);
        Set(tour, nameof(Tour.OperatorUserId), operatorId);
        Set(tour, nameof(Tour.Title), title);
        Set(tour, nameof(Tour.BasePrice), 500_000m);
        Set(tour, nameof(Tour.DurationDays), 2);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        return tour;
    }

    public static TourSchedule CreateTourSchedule(long id, long tourId)
    {
        var schedule = TourSchedule.Create(
            startAtUtc: new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero),
            endAtUtc: new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero),
            meetingPoint: null,
            totalCapacity: 20,
            createdAtUtc: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        Set(schedule, nameof(TourSchedule.Id), id);
        Set(schedule, nameof(TourSchedule.TourId), tourId);
        return schedule;
    }

    public static Refund CreateRefund(long id, long bookingId, decimal amount, DateTimeOffset processedAtUtc)
    {
        var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;
        Set(refund, nameof(Refund.Id), id);
        Set(refund, nameof(Refund.BookingId), bookingId);
        Set(refund, nameof(Refund.Amount), amount);
        Set(refund, nameof(Refund.Status), "Processed");
        Set(refund, nameof(Refund.ProcessedAtUtc), processedAtUtc);
        return refund;
    }

    public static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}