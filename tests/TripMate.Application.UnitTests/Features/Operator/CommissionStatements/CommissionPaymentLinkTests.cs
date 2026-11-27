using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Operator.CommissionStatements;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Operator.CommissionStatements;

public sealed class CommissionPaymentLinkTests
{
    [Fact]
    public async Task FailedCreationWaitsBeforeRetryAndOnlyReplacesAttemptAfterConfirmedCancellation()
    {
        await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var statement = DirectCommissionStatement.Issue("DCO-retry", 7, "2026-10", 100000, .1m, 10000, new(2026, 11, 1), now);
        db.DirectCommissionStatements.Add(statement); await db.SaveChangesAsync();
        var gateway = new FakePaymentGateway { ThrowOnCreate = new HttpRequestException("Unavailable") };
        var clock = new FakeDateTimeProvider { UtcNow = now };
        var handler = new CreateCommissionPaymentLinkCommandHandler(db, new FakeCurrentUserService { UserId = 7, Role = "TourOperator" }, gateway, clock);
        Assert.True((await handler.Handle(new(statement.Id), default)).IsFailure);
        Assert.Equal("commission.payment_in_progress", (await handler.Handle(new(statement.Id), default)).ErrorCode);
        clock.UtcNow = now.AddMinutes(3);
        gateway.ThrowOnCreate = null;
        gateway.ThrowOnCancel = new HttpRequestException("Unknown provider outcome");
        Assert.True((await handler.Handle(new(statement.Id), default)).IsFailure);
        Assert.Single(db.CommissionPaymentAttempts);
        Assert.Equal(CommissionPaymentAttempt.Creating, db.CommissionPaymentAttempts.Single().Status);
        gateway.ThrowOnCancel = null;
        Assert.True((await handler.Handle(new(statement.Id), default)).IsSuccess);
        Assert.Equal(2, await db.CommissionPaymentAttempts.CountAsync());
        Assert.Single(db.CommissionPaymentAttempts.Where(a => a.Status == CommissionPaymentAttempt.Pending));
        Assert.Single(db.CommissionPaymentAttempts.Where(a => a.Status == CommissionPaymentAttempt.Cancelled));
    }

    [Fact]
    public async Task ValidLinkIsReusedAndWrongOwnerCannotPay()
    {
        await using var db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var s = DirectCommissionStatement.Issue("DCO-7", 7, "2026-10", 100000, .1m, 10000, new(2026, 11, 1), now);
        db.DirectCommissionStatements.Add(s); await db.SaveChangesAsync();
        var user = new FakeCurrentUserService { UserId = 7, Role = "TourOperator" };
        var gateway = new FakePaymentGateway();
        var handler = new CreateCommissionPaymentLinkCommandHandler(db, user, gateway, new FakeDateTimeProvider { UtcNow = now });
        Assert.True((await handler.Handle(new(s.Id), default)).IsSuccess);
        Assert.True((await handler.Handle(new(s.Id), default)).IsSuccess);
        Assert.Equal(1, gateway.CreateCallCount);
        Assert.Single(db.CommissionPaymentAttempts);
        user.UserId = 8;
        Assert.Equal("commission.forbidden", (await handler.Handle(new(s.Id), default)).ErrorCode);
    }
}