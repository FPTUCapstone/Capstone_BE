using FluentAssertions;

using TripMate.Application.Features.Operator.Payouts.GetSettlementMonths;
using TripMate.Application.Features.Operator.Payouts.RequestPayout;
using TripMate.Application.UnitTests.Features.Operator.Payouts.RequestPayout;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.GetSettlementMonths;

public sealed class GetOperatorSettlementMonthsQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_ClosedMonthWithEligibleBooking_IsAvailableBalance()
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

        var handler = new GetOperatorSettlementMonthsQueryHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id), new RequestPayoutCommandHandlerTests.FixedClock(Now));

        var result = await handler.Handle(new GetOperatorSettlementMonthsQuery(2026), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var august = result.Value.Single(m => m.Month == 8);
        august.MonthStatus.Should().Be("Closed");
        august.EligibleBookingCount.Should().Be(1);
        august.Coverage.Should().Be("AvailableBalance");
        august.PayoutCode.Should().BeNull();
        august.PayableNetAmount.Should().Be(9_000_000m);
    }

    [Fact]
    public async Task Handle_OpenMonthWithBooking_ShowsOpenCoverage()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);
        var tour = PayoutTestFixture.CreateTour(101, operatorUser.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        // Schedule ends inside October, the same month as "now" (not closed yet).
        var schedule = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero));
        db.Add(schedule);
        await db.SaveChangesAsync();
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 301, schedule.Id, 10_000_000m, new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.Zero));

        var handler = new GetOperatorSettlementMonthsQueryHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id), new RequestPayoutCommandHandlerTests.FixedClock(Now));

        var result = await handler.Handle(new GetOperatorSettlementMonthsQuery(2026), CancellationToken.None);

        var october = result.Value.Single(m => m.Month == 10);
        october.MonthStatus.Should().Be("Open");
        october.Coverage.Should().Be("Open");
    }

    [Fact]
    public async Task Handle_MonthClaimedByActivePayout_ShowsIncludedInPayoutWithItsCode()
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
        var requestHandler = new RequestPayoutCommandHandler(db, currentUser, clock);
        var requested = await requestHandler.Handle(new RequestPayoutCommand(), CancellationToken.None);
        requested.IsSuccess.Should().BeTrue();

        var handler = new GetOperatorSettlementMonthsQueryHandler(db, currentUser, clock);
        var result = await handler.Handle(new GetOperatorSettlementMonthsQuery(2026), CancellationToken.None);

        var august = result.Value.Single(m => m.Month == 8);
        august.Coverage.Should().Be("IncludedInPayout");
        august.PayoutCode.Should().Be(requested.Value.PayoutCode);
        august.PayableNetAmount.Should().Be(9_000_000m);
    }

    [Fact]
    public async Task Handle_NoBookingsAtAll_ReturnsEmptyList()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);

        var handler = new GetOperatorSettlementMonthsQueryHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id), new RequestPayoutCommandHandlerTests.FixedClock(Now));

        var result = await handler.Handle(new GetOperatorSettlementMonthsQuery(2026), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
