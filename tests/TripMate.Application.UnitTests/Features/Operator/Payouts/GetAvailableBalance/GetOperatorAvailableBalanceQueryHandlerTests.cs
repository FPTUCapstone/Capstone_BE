using FluentAssertions;

using TripMate.Application.Features.Operator.Payouts.GetAvailableBalance;
using TripMate.Application.Features.Operator.Payouts.RequestPayout;
using TripMate.Application.UnitTests.Features.Operator.Payouts.RequestPayout;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.GetAvailableBalance;

public sealed class GetOperatorAvailableBalanceQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_TwoClosedMonths_AggregatesIntoOneBalance()
    {
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

        var handler = new GetOperatorAvailableBalanceQueryHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id), new RequestPayoutCommandHandlerTests.FixedClock(Now));

        var result = await handler.Handle(new GetOperatorAvailableBalanceQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PeriodCount.Should().Be(2);
        result.Value.PayableNetAmount.Should().Be(2_700_000m);
        result.Value.Requestable.Should().BeTrue();
        result.Value.BlockReason.Should().BeNull();
    }

    [Fact]
    public async Task Handle_NoEligibleBookings_ReturnsNoEligibleBookingBlockReason()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);

        var handler = new GetOperatorAvailableBalanceQueryHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id), new RequestPayoutCommandHandlerTests.FixedClock(Now));

        var result = await handler.Handle(new GetOperatorAvailableBalanceQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Requestable.Should().BeFalse();
        result.Value.BlockReason.Should().Be("NO_ELIGIBLE_BOOKING");
    }

    [Fact]
    public async Task Handle_ActivePayoutExists_ReturnsActivePayoutExistsAndItsCode()
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

        var currentUser = RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id);
        var clock = new RequestPayoutCommandHandlerTests.FixedClock(Now);
        var requested = await new RequestPayoutCommandHandler(db, currentUser, clock)
            .Handle(new RequestPayoutCommand(), CancellationToken.None);
        requested.IsSuccess.Should().BeTrue();

        var handler = new GetOperatorAvailableBalanceQueryHandler(db, currentUser, clock);
        var result = await handler.Handle(new GetOperatorAvailableBalanceQuery(), CancellationToken.None);

        result.Value.BlockReason.Should().Be("ACTIVE_PAYOUT_EXISTS");
        result.Value.ActivePayoutCode.Should().Be(requested.Value.PayoutCode);
    }
}
