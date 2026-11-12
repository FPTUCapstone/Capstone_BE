using System.Reflection;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Payouts;

/// <summary>Seeding helpers for the DAILY-PAYOUT-TRANSFER processor/sweep tests.</summary>
internal static class DailyPayoutTestFixture
{
    public static async Task<User> SeedOperatorAsync(TestDbContext db, long id, AccountStatus status = AccountStatus.Active)
    {
        var user = new User
        {
            Id = id,
            Email = $"operator{id}@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = status,
        };
        db.Users.Add(user);

        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = user.Id,
            User = user,
            CompanyName = "Company",
            TaxCode = $"TAX-{id}",
            BusinessLicenseNo = $"LIC-{id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CommissionRate = 10m,
            // SRS 3.10.5 step 1: a payout transfer requires a complete beneficiary bank account
            // (MSG146 otherwise) — set by default so existing payout-run tests still exercise a
            // real simulated transfer; DailyPayoutDebtAndAutoLockTests seeds its own operator
            // without one where that absence is the point of the test.
            BankName = "Test Bank",
            AccountHolderName = "Operator Holder",
            AccountNumberMasked = "****1234",
            AccountNumberProtected = "protected-test-value",
            BankAccountUpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return user;
    }

    private static Tour CreateTour(long id, long operatorId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.Id), id);
        Set(tour, nameof(Tour.OperatorUserId), operatorId);
        Set(tour, nameof(Tour.Title), "Test Tour");
        Set(tour, nameof(Tour.BasePrice), 500_000m);
        Set(tour, nameof(Tour.DurationDays), 2);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        return tour;
    }

    private static TourSchedule CreateTourSchedule(long id, long tourId, DateTimeOffset endAtUtc)
    {
        var schedule = TourSchedule.Create(
            startAtUtc: endAtUtc.AddDays(-1), endAtUtc: endAtUtc, meetingPoint: null,
            totalCapacity: 20, createdAtUtc: endAtUtc.AddMonths(-2));
        Set(schedule, nameof(TourSchedule.Id), id);
        Set(schedule, nameof(TourSchedule.TourId), tourId);
        return schedule;
    }

    public static async Task<long> SeedTourScheduleAsync(TestDbContext db, long tourId, long scheduleId, long operatorId, DateTimeOffset endAtUtc)
    {
        if (!db.Tours.Local.Any(t => t.Id == tourId) && !await db.Tours.AnyAsync(t => t.Id == tourId))
        {
            db.Tours.Add(CreateTour(tourId, operatorId));
        }
        db.TourSchedules.Add(CreateTourSchedule(scheduleId, tourId, endAtUtc));
        await db.SaveChangesAsync();
        return scheduleId;
    }

    /// <summary>Seeds a Confirmed+Paid booking with a verified payment carrying a commission
    /// snapshot — eligible for a Main settlement (PD-2).</summary>
    public static async Task<Booking> SeedConfirmedBookingAsync(
        TestDbContext db, long bookingId, long scheduleId, decimal totalAmount,
        DateTimeOffset succeededAtUtc, decimal commissionRateApplied = 10m)
    {
        var travelerId = 900_000 + bookingId;
        db.Users.Add(new User
        {
            Id = travelerId,
            Email = $"traveler{travelerId}@test.local",
            FullName = "Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
        });

        var bookedAt = succeededAtUtc.AddMinutes(-60);
        var participant = BookingParticipant.Create("Adult", ParticipantType.Adult, null, null, null, null, null);
        var booking = Booking.Create(
            bookingCode: $"BK-DP-{bookingId}", travelerUserId: travelerId, tourScheduleId: scheduleId,
            voucherId: null, unitPrice: totalAmount, discountAmount: 0m, totalAmount: totalAmount,
            participants: [participant], bookedAtUtc: bookedAt,
            paymentExpiresAtUtc: succeededAtUtc.AddMinutes(30));
        Set(booking, nameof(Booking.Id), bookingId);
        booking.MarkPaid(succeededAtUtc);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var transaction = PaymentTransaction.Create(
            bookingId: booking.Id, gateway: PaymentGateway.Mock, amount: totalAmount,
            orderCode: bookingId, createdAtUtc: bookedAt);
        transaction.MarkSuccess(gatewayRef: null, rawResponse: null, nowUtc: succeededAtUtc, commissionRateApplied: commissionRateApplied);
        db.PaymentTransactions.Add(transaction);
        await db.SaveChangesAsync();

        return booking;
    }

    /// <summary>Seeds a Cancelled booking whose refund, if any, left some amount retained — the
    /// Retained-kind case (PD-2, BR-142). Pass <paramref name="refundedAmount"/> = 0 for the
    /// "0% refund, everything retained" tier and <paramref name="refundedAmount"/> equal to
    /// <paramref name="totalAmount"/> to get a fully-refunded booking excluded entirely.</summary>
    public static async Task<Booking> SeedCancelledPartiallyRefundedBookingAsync(
        TestDbContext db, long bookingId, long scheduleId, decimal totalAmount, decimal refundedAmount,
        DateTimeOffset succeededAtUtc, DateTimeOffset cancelledAtUtc, decimal commissionRateApplied = 10m)
    {
        var booking = await SeedConfirmedBookingAsync(db, bookingId, scheduleId, totalAmount, succeededAtUtc, commissionRateApplied);
        booking.Cancel(cancelledAtUtc, "Traveler requested");
        booking.ApplyRefund(refundedAmount);

        if (refundedAmount > 0)
        {
            var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;
            Set(refund, nameof(Refund.Id), 500_000 + bookingId);
            Set(refund, nameof(Refund.BookingId), bookingId);
            Set(refund, nameof(Refund.Amount), refundedAmount);
            Set(refund, nameof(Refund.Status), RefundStatus.Processed);
            Set(refund, nameof(Refund.ProcessedAtUtc), cancelledAtUtc);
            db.Refunds.Add(refund);
        }

        await db.SaveChangesAsync();
        return booking;
    }

    /// <summary>Seeds a <c>Processed</c> refund against a still-Confirmed booking (e.g. UC-42
    /// partial refund) — does not cancel the booking, unlike <see cref="SeedCancelledPartiallyRefundedBookingAsync"/>.
    /// Used to verify BR-142: the Final settlement is computed on (PaidAmount − processed refunds).</summary>
    public static async Task SeedProcessedRefundAsync(TestDbContext db, long bookingId, decimal amount, DateTimeOffset processedAtUtc)
    {
        var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;
        Set(refund, nameof(Refund.Id), 600_000 + bookingId);
        Set(refund, nameof(Refund.BookingId), bookingId);
        Set(refund, nameof(Refund.Amount), amount);
        Set(refund, nameof(Refund.Status), RefundStatus.Processed);
        Set(refund, nameof(Refund.ProcessedAtUtc), processedAtUtc);
        db.Refunds.Add(refund);
        await db.SaveChangesAsync();
    }

    private static void Set(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property {propertyName} not found on {target.GetType()}.");
        property.SetValue(target, value);
    }
}