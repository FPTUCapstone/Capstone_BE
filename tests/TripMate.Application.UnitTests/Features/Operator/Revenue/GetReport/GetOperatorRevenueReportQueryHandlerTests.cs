using System.Reflection;

using FluentAssertions;

using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Revenue.GetReport;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Revenue.GetReport;

public sealed class GetOperatorRevenueReportQueryHandlerTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);
    private static readonly DateOnly End = new(2026, 9, 30);

    [Fact]
    public async Task Handle_InvalidGranularity_ReturnsInvalidGranularityError()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7);
        var handler = new GetOperatorRevenueReportQueryHandler(db, new FakeCurrentUserService
        {
            UserId = operatorUser.Id,
            Role = nameof(UserRole.TourOperator),
        }, new OperatorRevenueAggregator(db));

        var result = await handler.Handle(
            new GetOperatorRevenueReportQuery(Start, End, "yearly"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorRevenueErrorCodes.InvalidGranularity);
    }

    [Fact]
    public async Task Handle_WithRefundsAcrossMultipleTours_DistributesRefundedAmountPerTourAndPeriod()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7, commissionRate: 10m);

        var tourA = CreateTour(101, operatorUser.Id, "Tour A");
        var tourB = CreateTour(102, operatorUser.Id, "Tour B");
        db.AddRange(tourA, tourB);
        await db.SaveChangesAsync();

        var scheduleA = CreateTourSchedule(201, tourA.Id);
        var scheduleB = CreateTourSchedule(202, tourB.Id);
        db.AddRange(scheduleA, scheduleB);
        await db.SaveChangesAsync();

        // Tour A: one paid booking of 1,000,000 with a 300,000 refund processed in the same period.
        var bookingA = await SeedPaidBookingAsync(db, bookingId: 301, scheduleId: scheduleA.Id,
            totalAmount: 1_000_000m, succeededAtUtc: new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));
        db.Add(CreateRefund(401, bookingA.Id, 300_000m,
            processedAtUtc: new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero)));

        // Tour B: one paid booking of 2,000,000 with no refund.
        await SeedPaidBookingAsync(db, bookingId: 302, scheduleId: scheduleB.Id,
            totalAmount: 2_000_000m, succeededAtUtc: new DateTimeOffset(2026, 9, 15, 3, 0, 0, TimeSpan.Zero));

        await db.SaveChangesAsync();

        var handler = new GetOperatorRevenueReportQueryHandler(db, new FakeCurrentUserService
        {
            UserId = operatorUser.Id,
            Role = nameof(UserRole.TourOperator),
        }, new OperatorRevenueAggregator(db));

        var result = await handler.Handle(
            new GetOperatorRevenueReportQuery(Start, End, "monthly"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Commission is charged only on the amount retained after the refund.
        result.Value.Summary.RefundedAmount.Should().Be(300_000m);
        // Payments alone are not TO revenue. No payout settlement was seeded, so the operator
        // has not received cash yet even though the report still shows booking value/refunds.
        result.Value.Summary.NetAmount.Should().Be(0m);

        // P1: tour breakdown must carry the refund only against the tour it belongs to.
        var detailA = result.Value.TourDetails.Single(t => t.TourId == tourA.Id);
        var detailB = result.Value.TourDetails.Single(t => t.TourId == tourB.Id);
        detailA.RefundedAmount.Should().Be(300_000m);
        detailA.NetAmount.Should().Be(0m);
        detailB.RefundedAmount.Should().Be(0m);
        detailB.NetAmount.Should().Be(0m);

        // P1: chart bucket for September must also reflect the refund, not zero.
        var septemberBucket = result.Value.ChartData.Single(c => c.PeriodKey == "2026-09");
        septemberBucket.RefundedAmount.Should().Be(300_000m);
        septemberBucket.NetAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Handle_CancelledBookingWithProcessedHalfRefund_ReportsProvisionalThenSettledCancellationShare()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7, commissionRate: 30m);
        var tour = CreateTour(103, operatorUser.Id, "Cancelled tour");
        db.Add(tour);
        await db.SaveChangesAsync();

        var schedule = CreateTourSchedule(203, tour.Id);
        db.Add(schedule);
        await db.SaveChangesAsync();

        var booking = await SeedPaidBookingAsync(db, bookingId: 303, scheduleId: schedule.Id,
            totalAmount: 10_000m, succeededAtUtc: new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));
        Set(booking, nameof(Booking.Status), BookingStatus.Cancelled);
        Set(booking, nameof(Booking.PaymentStatus), PaymentStatus.PartiallyRefunded);
        var refund = CreateRefund(403, booking.Id, 5_000m,
            processedAtUtc: new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero));
        Set(refund, nameof(Refund.OperatorReturnShare), 3_500m);
        Set(refund, nameof(Refund.ReturnReceivedAtUtc), new DateTimeOffset(2026, 9, 12, 2, 0, 0, TimeSpan.Zero));
        db.Add(refund);

        var advanceSettlement = PayoutSettlement.Create(
            batchId: 700,
            bookingId: booking.Id,
            tourOperatorUserId: operatorUser.Id,
            runDate: new DateOnly(2026, 9, 11),
            kind: PayoutSettlementKind.Advance,
            grossAmount: 7_000m,
            commissionRate: 0m,
            commissionAmount: 0m,
            netAmount: 7_000m,
            debtOffsetAmount: 0m,
            payoutAmount: 7_000m,
            createdAtUtc: new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero));
        Set(advanceSettlement, nameof(PayoutSettlement.Status), PayoutSettlementStatus.Settled);
        db.PayoutSettlements.Add(advanceSettlement);
        await db.SaveChangesAsync();

        var handler = new GetOperatorRevenueReportQueryHandler(db, new FakeCurrentUserService
        {
            UserId = operatorUser.Id,
            Role = nameof(UserRole.TourOperator),
        }, new OperatorRevenueAggregator(db));

        var result = await handler.Handle(
            new GetOperatorRevenueReportQuery(Start, End, "monthly"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.PaymentsReceived.Should().Be(10_000m);
        result.Value.Summary.RefundedAmount.Should().Be(5_000m);
        result.Value.Summary.NetSales.Should().Be(5_000m);
        // A cancelled booking keeps only the configured 10% platform fee on its
        // retained 5,000 VND; the later cancellation settlement changes cash payout,
        // not the accounting commission.
        result.Value.Summary.PlatformCommission.Should().Be(500m);
        result.Value.Summary.NetAmount.Should().Be(3_500m);

        var detail = result.Value.TourDetails.Should().ContainSingle().Subject;
        detail.RefundedAmount.Should().Be(5_000m);
        detail.Commission.Should().Be(500m);
        detail.NetAmount.Should().Be(3_500m);

        var cancellationSettlement = PayoutSettlement.Create(
            batchId: 701,
            bookingId: booking.Id,
            tourOperatorUserId: operatorUser.Id,
            runDate: new DateOnly(2026, 9, 13),
            kind: PayoutSettlementKind.Cancellation,
            grossAmount: 5_000m,
            commissionRate: 10m,
            commissionAmount: 500m,
            netAmount: 4_500m,
            debtOffsetAmount: 0m,
            payoutAmount: 1_000m,
            createdAtUtc: new DateTimeOffset(2026, 9, 13, 3, 0, 0, TimeSpan.Zero));
        Set(cancellationSettlement, nameof(PayoutSettlement.Status), PayoutSettlementStatus.Settled);
        db.PayoutSettlements.Add(cancellationSettlement);
        await db.SaveChangesAsync();

        var settledResult = await handler.Handle(
            new GetOperatorRevenueReportQuery(Start, End, "monthly"),
            CancellationToken.None);

        settledResult.IsSuccess.Should().BeTrue();
        settledResult.Value.Summary.PlatformCommission.Should().Be(500m);
        settledResult.Value.Summary.NetAmount.Should().Be(4_500m);
    }

    [Fact]
    public async Task Handle_CancelledPaidBookingWithSettledAdvance_KeepsTransferredCashVisibleDuringReconciliation()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7, commissionRate: 10m);
        var tour = CreateTour(105, operatorUser.Id, "Return pending tour");
        db.Add(tour);
        await db.SaveChangesAsync();

        var schedule = CreateTourSchedule(205, tour.Id);
        db.Add(schedule);
        await db.SaveChangesAsync();

        var booking = await SeedPaidBookingAsync(db, bookingId: 305, scheduleId: schedule.Id,
            totalAmount: 10_000m, succeededAtUtc: new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));
        Set(booking, nameof(Booking.Status), BookingStatus.Cancelled);
        // The refund is still being reconciled, so payment status is deliberately Paid.
        Set(booking, nameof(Booking.PaymentStatus), PaymentStatus.Paid);

        var advanceSettlement = PayoutSettlement.Create(
            batchId: 702,
            bookingId: booking.Id,
            tourOperatorUserId: operatorUser.Id,
            runDate: new DateOnly(2026, 9, 11),
            kind: PayoutSettlementKind.Advance,
            grossAmount: 7_000m,
            commissionRate: 0m,
            commissionAmount: 0m,
            netAmount: 7_000m,
            debtOffsetAmount: 0m,
            payoutAmount: 7_000m,
            createdAtUtc: new DateTimeOffset(2026, 9, 11, 3, 0, 0, TimeSpan.Zero));
        Set(advanceSettlement, nameof(PayoutSettlement.Status), PayoutSettlementStatus.Settled);
        db.PayoutSettlements.Add(advanceSettlement);
        await db.SaveChangesAsync();

        var handler = new GetOperatorRevenueReportQueryHandler(db, new FakeCurrentUserService
        {
            UserId = operatorUser.Id,
            Role = nameof(UserRole.TourOperator),
        }, new OperatorRevenueAggregator(db));

        var result = await handler.Handle(
            new GetOperatorRevenueReportQuery(Start, End, "monthly"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.PaymentsReceived.Should().Be(10_000m);
        result.Value.Summary.RefundedAmount.Should().Be(0m);
        result.Value.Summary.NetAmount.Should().Be(7_000m);
    }

    [Fact]
    public async Task Handle_BookingFullyRefunded_ChargesZeroCommissionNotNegativePayout()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7, commissionRate: 10m);

        var tour = CreateTour(103, operatorUser.Id, "Tour C");
        db.Add(tour);
        await db.SaveChangesAsync();

        var schedule = CreateTourSchedule(203, tour.Id);
        db.Add(schedule);
        await db.SaveChangesAsync();

        var booking = await SeedPaidBookingAsync(db, bookingId: 303, scheduleId: schedule.Id,
            totalAmount: 1_000_000m, succeededAtUtc: new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero));
        db.Add(CreateRefund(402, booking.Id, 1_000_000m,
            processedAtUtc: new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var handler = new GetOperatorRevenueReportQueryHandler(db, new FakeCurrentUserService
        {
            UserId = operatorUser.Id,
            Role = nameof(UserRole.TourOperator),
        }, new OperatorRevenueAggregator(db));

        var result = await handler.Handle(
            new GetOperatorRevenueReportQuery(Start, End, "monthly"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.PaymentsReceived.Should().Be(1_000_000m);
        result.Value.Summary.RefundedAmount.Should().Be(1_000_000m);
        result.Value.Summary.PlatformCommission.Should().Be(0m);
        result.Value.Summary.NetAmount.Should().Be(0m);

        var detail = result.Value.TourDetails.Single(t => t.TourId == tour.Id);
        detail.Commission.Should().Be(0m);
        detail.NetAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Handle_RefundProcessedInLaterPeriod_StillZeroesCommissionInPaymentPeriod()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await SeedApprovedOperatorAsync(db, 7, commissionRate: 10m);

        var tour = CreateTour(104, operatorUser.Id, "Tour D");
        db.Add(tour);
        await db.SaveChangesAsync();

        var schedule = CreateTourSchedule(204, tour.Id);
        db.Add(schedule);
        await db.SaveChangesAsync();

        var booking = await SeedPaidBookingAsync(db, bookingId: 304, scheduleId: schedule.Id,
            totalAmount: 1_000_000m, succeededAtUtc: new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        db.Add(CreateRefund(403, booking.Id, 1_000_000m,
            processedAtUtc: new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var handler = new GetOperatorRevenueReportQueryHandler(db, new FakeCurrentUserService
        {
            UserId = operatorUser.Id,
            Role = nameof(UserRole.TourOperator),
        }, new OperatorRevenueAggregator(db));

        var septemberResult = await handler.Handle(
            new GetOperatorRevenueReportQuery(Start, End, "monthly"),
            CancellationToken.None);

        septemberResult.IsSuccess.Should().BeTrue();
        septemberResult.Value.Summary.PaymentsReceived.Should().Be(1_000_000m);
        septemberResult.Value.Summary.RefundedAmount.Should().Be(1_000_000m);
        septemberResult.Value.Summary.PlatformCommission.Should().Be(0m);
        septemberResult.Value.Summary.NetAmount.Should().Be(0m);

        var octoberResult = await handler.Handle(
            new GetOperatorRevenueReportQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "monthly"),
            CancellationToken.None);

        octoberResult.IsSuccess.Should().BeTrue();
        octoberResult.Value.Summary.PaymentsReceived.Should().Be(0m);
        octoberResult.Value.Summary.PlatformCommission.Should().Be(0m);
        octoberResult.Value.TourDetails.Should().BeEmpty();
    }

    private static async Task<User> SeedApprovedOperatorAsync(TestDbContext db, long id, decimal commissionRate = 10m)
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

    private static async Task<Booking> SeedPaidBookingAsync(
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

    private static Tour CreateTour(long id, long operatorId, string title)
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

    private static TourSchedule CreateTourSchedule(long id, long tourId)
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

    private static Refund CreateRefund(long id, long bookingId, decimal amount, DateTimeOffset processedAtUtc)
    {
        var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;
        Set(refund, nameof(Refund.Id), id);
        Set(refund, nameof(Refund.BookingId), bookingId);
        Set(refund, nameof(Refund.Amount), amount);
        Set(refund, nameof(Refund.Status), RefundStatus.Processed);
        Set(refund, nameof(Refund.ProcessedAtUtc), processedAtUtc);
        return refund;
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}