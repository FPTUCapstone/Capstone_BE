using System.Reflection;

using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts;

/// <summary>Shared seeding helpers for UC-46 payout handler tests.</summary>
internal static class PayoutTestFixture
{
    public static async Task<User> SeedApprovedOperatorAsync(
        TestDbContext db, long id = 7, decimal commissionRate = 10m, bool withBankAccount = true)
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

        var profile = new OperatorProfile
        {
            UserId = user.Id,
            User = user,
            CompanyName = "Company",
            TaxCode = $"TAX-{id}",
            BusinessLicenseNo = $"LIC-{id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
            CommissionRate = commissionRate,
        };
        if (withBankAccount)
        {
            profile.UpdateBeneficiaryBankAccount(
                "Vietcombank", "Nguyen Van A", "****1234", "protected:1029384756", DateTimeOffset.UtcNow);
        }

        db.OperatorProfiles.Add(profile);
        await db.SaveChangesAsync();
        return user;
    }

    public static Tour CreateTour(long id, long operatorId, string title = "Test Tour")
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

    public static TourSchedule CreateTourSchedule(long id, long tourId, DateTimeOffset endAtUtc)
    {
        var schedule = TourSchedule.Create(
            startAtUtc: endAtUtc.AddDays(-1),
            endAtUtc: endAtUtc,
            meetingPoint: null,
            totalCapacity: 20,
            createdAtUtc: endAtUtc.AddMonths(-2));
        Set(schedule, nameof(TourSchedule.Id), id);
        Set(schedule, nameof(TourSchedule.TourId), tourId);
        return schedule;
    }

    /// <summary>Seeds a Completed booking with a successful payment, as required for UC-46
    /// eligibility (status Completed, verified payment).</summary>
    public static async Task<(Booking Booking, long PaymentTransactionId)> SeedCompletedBookingAsync(
        TestDbContext db, long bookingId, long scheduleId, decimal totalAmount,
        DateTimeOffset succeededAtUtc, decimal commissionRateApplied = 10m)
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
        Set(booking, nameof(Booking.Status), BookingStatus.Completed);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var transaction = PaymentTransaction.Create(
            bookingId: booking.Id,
            gateway: PaymentGateway.Mock,
            amount: totalAmount,
            orderCode: bookingId,
            createdAtUtc: bookedAt);
        transaction.MarkSuccess(gatewayRef: null, rawResponse: null, nowUtc: succeededAtUtc, commissionRateApplied: commissionRateApplied);
        db.PaymentTransactions.Add(transaction);
        await db.SaveChangesAsync();

        return (booking, transaction.Id);
    }

    /// <summary>Seeds a PendingReview payout for one settlement month — enough for admin
    /// review (UC-64/73/74) handlers, which only read/transition the header row.</summary>
    public static async Task<Payout> SeedPendingReviewPayoutAsync(
        TestDbContext db, long operatorUserId, DateOnly settlementMonth,
        decimal paymentsReceived = 10_000_000m, decimal commissionAmount = 1_000_000m,
        DateTimeOffset? requestedAtUtc = null)
    {
        var payout = Payout.Create(
            payoutCode: $"PO-TEST-{Guid.NewGuid():N}"[..24],
            operatorUserId: operatorUserId,
            months: [new PayoutMonthTotal(
                settlementMonth, EligibleBookingCount: 1, ParticipantCount: 1,
                PaymentsReceived: paymentsReceived, DiscountsGiven: 0m, RefundedAmount: 0m,
                CommissionAmount: commissionAmount, PayableNetAmount: paymentsReceived - commissionAmount)],
            cutOffAtUtc: requestedAtUtc ?? DateTimeOffset.UtcNow,
            adjustmentsApplied: 0m,
            minimumAmountChecked: 1_000_000m,
            bankName: "Vietcombank",
            accountHolderName: "Nguyen Van A",
            accountNumberMasked: "****1234",
            accountNumberProtected: "protected:1029384756",
            requestedAtUtc: requestedAtUtc ?? DateTimeOffset.UtcNow);

        db.Payouts.Add(payout);
        await db.SaveChangesAsync();
        payout.AssignPayoutCode($"{Payout.PayoutCodePrefix}{payout.Id:D6}");
        await db.SaveChangesAsync();
        return payout;
    }

    public static Refund CreateProcessedRefund(long id, long bookingId, decimal amount, DateTimeOffset processedAtUtc)
    {
        var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;
        Set(refund, nameof(Refund.Id), id);
        Set(refund, nameof(Refund.BookingId), bookingId);
        Set(refund, nameof(Refund.Amount), amount);
        Set(refund, nameof(Refund.Status), RefundStatus.Processed);
        Set(refund, nameof(Refund.ProcessedAtUtc), processedAtUtc);
        return refund;
    }

    public static void Set<T>(object instance, string property, T value) =>
        instance.GetType()
            .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}