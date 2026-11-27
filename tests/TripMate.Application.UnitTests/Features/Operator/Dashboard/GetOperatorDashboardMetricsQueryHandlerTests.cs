using FluentAssertions;

using TripMate.Application.Features.Operator.Dashboard;
using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Operator.Tours.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

using static TripMate.Application.UnitTests.TestUtilities.OperatorRevenueTestFixtures;

namespace TripMate.Application.UnitTests.Features.Operator.Dashboard;

public sealed class GetOperatorDashboardMetricsQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 5, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, null)]
    [InlineData(7L, "Traveler")]
    [InlineData(8L, "TourOperator")]
    public async Task Handle_WithoutEligibleOperator_DeniesAccess(long? userId, string? role)
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        var result = await Handler(db, userId, role).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorTourErrorCodes.AccessRequired);
    }

    [Fact]
    public async Task Handle_InactiveOrUnapprovedOperator_DeniesAccess()
    {
        await using var db = TestDbContext.Create();
        var owner = await SeedApprovedOperatorAsync(db, 7);
        owner.Status = AccountStatus.Locked;
        await db.SaveChangesAsync();

        var inactive = await Handler(db).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        inactive.IsSuccess.Should().BeFalse();
        owner.Status = AccountStatus.Active;
        db.OperatorProfiles.Single().ApprovalStatus = OperatorApprovalStatus.PendingApproval;
        await db.SaveChangesAsync();

        var pending = await Handler(db).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        pending.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_CountsEachOwnedPublishedTourOnlyOnce_WhenDepartureIsOpen()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        await SeedApprovedOperatorAsync(db, 8);
        var owned = CreateTour(101, 7, "Open");
        var other = CreateTour(102, 8, "Other operator");
        var draft = CreateTour(103, 7, "Draft");
        Set(draft, nameof(Tour.Status), TourStatus.Draft);
        db.Tours.AddRange(owned, other, draft);
        db.TourSchedules.AddRange(Schedule(201, 101), Schedule(202, 101), Schedule(203, 102), Schedule(204, 103));
        await db.SaveChangesAsync();

        var result = await Handler(db).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ActiveTourCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_CountsApprovedTourOnlyAfterItsPublicationInstant()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        var live = CreateTour(101, 7, "Live approved");
        Set(live, nameof(Tour.Status), TourStatus.Approved);
        Set(live, nameof(Tour.PublishedAtUtc), (DateTimeOffset?)Now.AddMinutes(-1));
        var scheduled = CreateTour(102, 7, "Not yet published");
        Set(scheduled, nameof(Tour.Status), TourStatus.Approved);
        Set(scheduled, nameof(Tour.PublishedAtUtc), (DateTimeOffset?)Now.AddMinutes(1));
        db.Tours.AddRange(live, scheduled);
        db.TourSchedules.AddRange(Schedule(201, 101), Schedule(202, 102));
        await db.SaveChangesAsync();

        var result = await Handler(db).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        result.Value.ActiveTourCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_ExcludesFullClosedCancelledAndAlreadyDepartedTours()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        var full = Schedule(201, 101);
        Set(full, nameof(TourSchedule.ReservedCapacity), full.TotalCapacity);
        var closed = Schedule(202, 102);
        Set(closed, nameof(TourSchedule.RegistrationDeadlineAtUtc), (DateTimeOffset?)Now);
        var cancelled = Schedule(203, 103);
        Set(cancelled, nameof(TourSchedule.Status), TourScheduleStatus.Cancelled);
        var departed = Schedule(204, 104);
        Set(departed, nameof(TourSchedule.StartAtUtc), Now);
        for (var id = 101; id <= 104; id++) db.Tours.Add(CreateTour(id, 7, $"Tour {id}"));
        db.TourSchedules.AddRange(full, closed, cancelled, departed);
        await db.SaveChangesAsync();

        var result = await Handler(db).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        result.Value.ActiveTourCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_CountsPaidOrdersByVietnamCreationMonth_OncePerOrder()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        await SeedApprovedOperatorAsync(db, 8);
        db.Tours.AddRange(CreateTour(101, 7, "Owned"), CreateTour(102, 8, "Other"));
        db.TourSchedules.AddRange(Schedule(201, 101), Schedule(202, 102));
        await db.SaveChangesAsync();
        var from = new DateTimeOffset(2026, 8, 31, 17, 0, 0, TimeSpan.Zero);
        var until = from.AddMonths(1);
        var first = await SeedPaidBookingAsync(db, 301, 201, 100_000m, Now);
        Set(first, nameof(Booking.BookedAtUtc), from);
        Set(first, nameof(Booking.PaymentStatus), PaymentStatus.PartiallyRefunded);
        var duplicate = PaymentTransaction.Create(first.Id, PaymentGateway.Mock, 100_000m, 9901, Now);
        duplicate.MarkSuccess(null, null, Now, 10m);
        db.PaymentTransactions.Add(duplicate);
        var before = await SeedPaidBookingAsync(db, 302, 201, 100_000m, Now);
        Set(before, nameof(Booking.BookedAtUtc), from.AddTicks(-1));
        var after = await SeedPaidBookingAsync(db, 303, 201, 100_000m, Now);
        Set(after, nameof(Booking.BookedAtUtc), until);
        var cancelled = await SeedPaidBookingAsync(db, 304, 201, 100_000m, Now);
        Set(cancelled, nameof(Booking.Status), BookingStatus.Cancelled);
        await SeedPaidBookingAsync(db, 305, 202, 100_000m, Now);
        var unpaid = await SeedPaidBookingAsync(db, 306, 201, 100_000m, Now);
        Set(unpaid, nameof(Booking.Status), BookingStatus.PendingPayment);
        var failed = await SeedPaidBookingAsync(db, 307, 201, 100_000m, Now);
        foreach (var payment in db.PaymentTransactions.Where(x => x.BookingId == failed.Id))
            Set(payment, nameof(PaymentTransaction.Status), PaymentTransactionStatus.Failed);
        await db.SaveChangesAsync();

        var result = await Handler(db).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        result.Value.MonthlyBookingCount.Should().Be(1);
        result.Value.MonthStart.Should().Be(new DateOnly(2026, 9, 1));
        result.Value.MonthEndExclusive.Should().Be(new DateOnly(2026, 10, 1));
        result.Value.AsOfUtc.Should().Be(Now);
    }

    [Fact]
    public async Task Handle_CountsOnlyOwnedActiveCurrentlyValidNonExhaustedCoupons()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        await SeedApprovedOperatorAsync(db, 8);
        var tour = CreateTour(101, 7, "Owned");
        db.Tours.Add(tour);
        var active = Coupon("ACTIVE", 7, tour);
        Set(active, nameof(Voucher.ValidFromUtc), Now);
        var end = Coupon("END", 7, tour);
        Set(end, nameof(Voucher.ValidToUtc), Now);
        var future = Coupon("FUTURE", 7, tour);
        Set(future, nameof(Voucher.ValidFromUtc), Now.AddTicks(1));
        var disabled = Coupon("DISABLED", 7, tour);
        Set(disabled, nameof(Voucher.Status), VoucherStatus.Disabled);
        var exhausted = Coupon("USED", 7, tour);
        Set(exhausted, nameof(Voucher.UsageLimit), (int?)1);
        Set(exhausted, nameof(Voucher.UsedCount), 1);
        db.Vouchers.AddRange(active, end, future, disabled, exhausted, Coupon("OTHER", 8, tour));
        await db.SaveChangesAsync();

        var result = await Handler(db).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        result.Value.ActiveCouponCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_ReusesExistingRevenueNetAmount_IncludingSettledPayoutAndReturn()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        db.Tours.Add(CreateTour(101, 7, "Owned"));
        db.TourSchedules.Add(Schedule(201, 101));
        await db.SaveChangesAsync();
        var booking = await SeedPaidBookingAsync(db, 301, 201, 100_000m, Now);
        var settlement = PayoutSettlement.Create(700, booking.Id, 7, new DateOnly(2026, 9, 15),
            PayoutSettlementKind.Advance, 70_000m, 0m, 0m, 70_000m, 0m, 70_000m, Now);
        Set(settlement, nameof(PayoutSettlement.Status), PayoutSettlementStatus.Settled);
        db.PayoutSettlements.Add(settlement);
        var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;
        Set(refund, nameof(Refund.Id), 801L);
        Set(refund, nameof(Refund.BookingId), booking.Id);
        Set(refund, nameof(Refund.Amount), 20_000m);
        Set(refund, nameof(Refund.Status), RefundStatus.Processed);
        Set(refund, nameof(Refund.ProcessedAtUtc), (DateTimeOffset?)Now);
        Set(refund, nameof(Refund.OperatorReturnShare), 14_000m);
        Set(refund, nameof(Refund.ReturnReceivedAtUtc), (DateTimeOffset?)Now);
        db.Refunds.Add(refund);
        await db.SaveChangesAsync();
        var expected = await new OperatorRevenueAggregator(db).AggregateAsync(7,
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "monthly", null, CancellationToken.None);

        var result = await Handler(db).Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        result.Value.MonthlyNetAmount.Should().Be(expected.Summary.NetAmount).And.Be(56_000m);
    }

    [Fact]
    public async Task Handle_UsesVietnamMonth_WhenUtcDateIsStillPreviousMonth()
    {
        await using var db = TestDbContext.Create();
        await SeedApprovedOperatorAsync(db, 7);
        var now = new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero);
        var handler = new GetOperatorDashboardMetricsQueryHandler(db,
            new FakeCurrentUserService { UserId = 7, Role = nameof(UserRole.TourOperator) },
            new FakeDateTimeProvider { UtcNow = now }, new OperatorRevenueAggregator(db));

        var result = await handler.Handle(new GetOperatorDashboardMetricsQuery(), CancellationToken.None);

        result.Value.MonthStart.Should().Be(new DateOnly(2027, 1, 1));
        result.Value.MonthEndExclusive.Should().Be(new DateOnly(2027, 2, 1));
        result.Value.MonthlyNetAmount.Should().Be(0);
    }

    private static GetOperatorDashboardMetricsQueryHandler Handler(TestDbContext db, long? userId = 7,
        string? role = nameof(UserRole.TourOperator)) => new(db,
            new FakeCurrentUserService { UserId = userId, Role = role },
            new FakeDateTimeProvider { UtcNow = Now }, new OperatorRevenueAggregator(db));

    private static TourSchedule Schedule(long id, long tourId) => CreateTourSchedule(id, tourId);

    private static Voucher Coupon(string code, long ownerId, Tour tour) => Voucher.Create(ownerId,
        code, VoucherDiscountType.Flat, 10_000m, null, 100_000m, null, null,
        Now.AddDays(-1), Now.AddDays(1), [tour], Now.AddDays(-2));
}