using FluentAssertions;

using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Application.Features.Operator.Payouts.RequestPayout;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.RequestPayout;

public sealed class RequestPayoutCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_SingleClosedMonth_CreatesPendingReviewPayout()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);
        var tour = PayoutTestFixture.CreateTour(101, operatorUser.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        var schedule = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        db.Add(schedule);
        await db.SaveChangesAsync();
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, bookingId: 301, scheduleId: schedule.Id, totalAmount: 10_000_000m,
            succeededAtUtc: new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero));

        var handler = new RequestPayoutCommandHandler(db, CurrentUser(operatorUser.Id), new FixedClock(Now));

        var result = await handler.Handle(new RequestPayoutCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("PendingReview");
        result.Value.PeriodCount.Should().Be(1);
        result.Value.PaymentsReceived.Should().Be(10_000_000m);
        result.Value.CommissionAmount.Should().Be(1_000_000m);
        result.Value.PayableNetAmount.Should().Be(9_000_000m);
        result.Value.Months.Should().ContainSingle(m => m.Year == 2026 && m.Month == 8);
        result.Value.PayoutCode.Should().StartWith("PO-");
    }

    [Fact]
    public async Task Handle_TwoClosedMonthsWithSmallAmounts_AggregatesIntoOnePayoutAboveMinimum()
    {
        // Each month alone (900,000 and 1,800,000) might individually sit below some minimums;
        // the "available balance" model combines them into one request instead of leaving a
        // low-volume operator stuck forever.
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);
        var tour = PayoutTestFixture.CreateTour(101, operatorUser.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        var scheduleAug = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        var scheduleSep = PayoutTestFixture.CreateTourSchedule(202, tour.Id, endAtUtc: new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero));
        db.AddRange(scheduleAug, scheduleSep);
        await db.SaveChangesAsync();
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 301, scheduleAug.Id, 1_000_000m, new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero));
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 302, scheduleSep.Id, 2_000_000m, new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero));

        var handler = new RequestPayoutCommandHandler(db, CurrentUser(operatorUser.Id), new FixedClock(Now));

        var result = await handler.Handle(new RequestPayoutCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PeriodCount.Should().Be(2);
        result.Value.PaymentsReceived.Should().Be(3_000_000m);
        result.Value.PayableNetAmount.Should().Be(2_700_000m); // 3,000,000 - 10% commission
        result.Value.Months.Should().HaveCount(2);
        result.Value.Months.Should().Contain(m => m.Year == 2026 && m.Month == 8 && m.PayableNetAmount == 900_000m);
        result.Value.Months.Should().Contain(m => m.Year == 2026 && m.Month == 9 && m.PayableNetAmount == 1_800_000m);
    }

    [Fact]
    public async Task Handle_OnlyOpenMonth_ReturnsNoEligibleBooking()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);
        var tour = PayoutTestFixture.CreateTour(101, operatorUser.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        // Schedule ends inside October, the same month as "now" — October has not closed yet.
        var schedule = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
        db.Add(schedule);
        await db.SaveChangesAsync();
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 301, schedule.Id, 10_000_000m, new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero));

        var handler = new RequestPayoutCommandHandler(db, CurrentUser(operatorUser.Id), new FixedClock(Now));

        var result = await handler.Handle(new RequestPayoutCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorPayoutErrorCodes.NoEligibleBooking);
    }

    [Fact]
    public async Task Handle_SecondRequestWhileFirstPending_ReturnsActivePayoutExists()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);
        var tour = PayoutTestFixture.CreateTour(101, operatorUser.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        var schedule = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        db.Add(schedule);
        await db.SaveChangesAsync();
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 301, schedule.Id, 10_000_000m, new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero));

        var handler = new RequestPayoutCommandHandler(db, CurrentUser(operatorUser.Id), new FixedClock(Now));
        var first = await handler.Handle(new RequestPayoutCommand(), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await handler.Handle(new RequestPayoutCommand(), CancellationToken.None);

        second.IsSuccess.Should().BeFalse();
        second.ErrorCode.Should().Be(OperatorPayoutErrorCodes.ActivePayoutExists);
    }

    [Fact]
    public async Task Handle_NoBeneficiaryBankAccount_ReturnsValidationRequiredField()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db, withBankAccount: false);
        var tour = PayoutTestFixture.CreateTour(101, operatorUser.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        var schedule = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        db.Add(schedule);
        await db.SaveChangesAsync();
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 301, schedule.Id, 10_000_000m, new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero));

        var handler = new RequestPayoutCommandHandler(db, CurrentUser(operatorUser.Id), new FixedClock(Now));

        var result = await handler.Handle(new RequestPayoutCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorPayoutErrorCodes.ValidationRequiredField);
    }

    [Fact]
    public async Task Handle_CombinedPayableBelowMinimum_ReturnsBelowMinimumPayout()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);
        var tour = PayoutTestFixture.CreateTour(101, operatorUser.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        var schedule = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        db.Add(schedule);
        await db.SaveChangesAsync();
        // 500,000 payment, 10% commission => payable 450,000, below the 1,000,000 default minimum.
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 301, schedule.Id, 500_000m, new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero));

        var handler = new RequestPayoutCommandHandler(db, CurrentUser(operatorUser.Id), new FixedClock(Now));

        var result = await handler.Handle(new RequestPayoutCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorPayoutErrorCodes.BelowMinimumPayout);
    }

    [Fact]
    public async Task Handle_RefundProcessedForEligibleBooking_ReducesPayableAndCommission()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);
        var tour = PayoutTestFixture.CreateTour(101, operatorUser.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        var schedule = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        db.Add(schedule);
        await db.SaveChangesAsync();
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 301, schedule.Id, 10_000_000m, new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero));
        db.Add(PayoutTestFixture.CreateProcessedRefund(
            401, bookingId: 301, amount: 2_000_000m, processedAtUtc: new DateTimeOffset(2026, 8, 18, 3, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync();

        var handler = new RequestPayoutCommandHandler(db, CurrentUser(operatorUser.Id), new FixedClock(Now));

        var result = await handler.Handle(new RequestPayoutCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RefundedAmount.Should().Be(2_000_000m);
        result.Value.CommissionAmount.Should().Be(800_000m); // 1,000,000 - round(2,000,000*10%)
        result.Value.PayableNetAmount.Should().Be(7_200_000m); // (10,000,000-2,000,000) - 800,000
    }

    internal static TripMate.Application.UnitTests.TestUtilities.FakeCurrentUserService CurrentUser(long operatorId) =>
        new() { UserId = operatorId, Role = nameof(UserRole.TourOperator) };

    internal sealed class FixedClock(DateTimeOffset now) : TripMate.Application.Common.Interfaces.IDateTimeProvider
    {
        public DateTimeOffset UtcNow => now;
    }
}
