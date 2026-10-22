using FluentAssertions;

using TripMate.Application.Features.Operator.Payouts.CancelPayout;
using TripMate.Application.Features.Operator.Payouts.Common;
using TripMate.Application.Features.Operator.Payouts.RequestPayout;
using TripMate.Application.UnitTests.Features.Operator.Payouts.RequestPayout;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Operator.Payouts.CancelPayout;

public sealed class CancelPayoutCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_PendingReviewPayout_CancelsSuccessfully()
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

        var cancelHandler = new CancelPayoutCommandHandler(db, currentUser, clock);
        var result = await cancelHandler.Handle(new CancelPayoutCommand(requested.Value.PayoutCode), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("Cancelled");

        // The booking must become claimable again: a second request succeeds.
        var secondRequest = await requestHandler.Handle(new RequestPayoutCommand(), CancellationToken.None);
        secondRequest.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_UnknownPayoutCode_ReturnsNotFound()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = await PayoutTestFixture.SeedApprovedOperatorAsync(db);
        var handler = new CancelPayoutCommandHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorUser.Id),
            new RequestPayoutCommandHandlerTests.FixedClock(Now));

        var result = await handler.Handle(new CancelPayoutCommand("PO-DOES-NOT-EXIST"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorPayoutErrorCodes.PayoutNotFound);
    }

    [Fact]
    public async Task Handle_ForeignOperatorsPayoutCode_ReturnsNotFound()
    {
        await using var db = TestDbContext.Create();
        var operatorA = await PayoutTestFixture.SeedApprovedOperatorAsync(db, id: 7);
        var operatorB = await PayoutTestFixture.SeedApprovedOperatorAsync(db, id: 8);
        var tour = PayoutTestFixture.CreateTour(101, operatorA.Id);
        db.Add(tour);
        await db.SaveChangesAsync();
        var schedule = PayoutTestFixture.CreateTourSchedule(201, tour.Id, endAtUtc: new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        db.Add(schedule);
        await db.SaveChangesAsync();
        await PayoutTestFixture.SeedCompletedBookingAsync(
            db, 301, schedule.Id, 10_000_000m, new DateTimeOffset(2026, 8, 15, 3, 0, 0, TimeSpan.Zero));

        var clock = new RequestPayoutCommandHandlerTests.FixedClock(Now);
        var requestHandler = new RequestPayoutCommandHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorA.Id), clock);
        var requested = await requestHandler.Handle(new RequestPayoutCommand(), CancellationToken.None);
        requested.IsSuccess.Should().BeTrue();

        var cancelHandler = new CancelPayoutCommandHandler(
            db, RequestPayoutCommandHandlerTests.CurrentUser(operatorB.Id), clock);
        var result = await cancelHandler.Handle(new CancelPayoutCommand(requested.Value.PayoutCode), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(OperatorPayoutErrorCodes.PayoutNotFound);
    }
}
