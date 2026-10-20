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
        result.Value.Summary.NetAmount.Should().Be(
            (1_000_000m - 70_000m - 300_000m) + (2_000_000m - 200_000m));

        // P1: tour breakdown must carry the refund only against the tour it belongs to.
        var detailA = result.Value.TourDetails.Single(t => t.TourId == tourA.Id);
        var detailB = result.Value.TourDetails.Single(t => t.TourId == tourB.Id);
        detailA.RefundedAmount.Should().Be(300_000m);
        detailA.NetAmount.Should().Be(1_000_000m - 70_000m - 300_000m);
        detailB.RefundedAmount.Should().Be(0m);
        detailB.NetAmount.Should().Be(2_000_000m - 200_000m);

        // P1: chart bucket for September must also reflect the refund, not zero.
        var septemberBucket = result.Value.ChartData.Single(c => c.PeriodKey == "2026-09");
        septemberBucket.RefundedAmount.Should().Be(300_000m);
        septemberBucket.NetAmount.Should().Be(
            (1_000_000m - 70_000m - 300_000m) + (2_000_000m - 200_000m));
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
