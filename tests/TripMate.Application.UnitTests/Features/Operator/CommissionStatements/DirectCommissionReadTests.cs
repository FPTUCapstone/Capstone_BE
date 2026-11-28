using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.CommissionStatements;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.Operator.CommissionStatements;

public sealed class DirectCommissionReadTests
{
    [Fact]
    public async Task ZeroPayableStatementNeverBecomesAnOverdueDebtOrOffersPayment()
    {
        await using var db = Db();
        var s = DirectCommissionStatement.Issue("DCO-zero", 10, "2026-10", 0, .1m, 0, new(2026, 11, 1), new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        db.DirectCommissionStatements.Add(s); await db.SaveChangesAsync();
        var result = await new GetDirectCommissionStatementQueryHandler(db, new FakeCurrentUserService { UserId = 10, Role = "TourOperator" },
            clock: new FakeDateTimeProvider { UtcNow = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) }).Handle(new(s.Id), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(DirectCommissionStatement.StatusIssued, result.Value!.Status);
        Assert.Empty(result.Value.AllowedActions);
    }

    [Fact]
    public async Task OperatorListAndDetailNeverExposeAnotherOperatorsStatement()
    {
        await using var db = Db();
        db.DirectCommissionStatements.AddRange(Statement(10, "2026-10"), Statement(11, "2026-10"));
        await db.SaveChangesAsync();
        var user = new FakeCurrentUserService { UserId = 10, Role = "TourOperator" };
        var list = await new ListDirectCommissionStatementsQueryHandler(db, user).Handle(new(), default);
        Assert.True(list.IsSuccess);
        Assert.Equal(10, Assert.Single(list.Value!.Items).TourOperatorUserId);
        long foreign = await db.DirectCommissionStatements.Where(x => x.TourOperatorUserId == 11).Select(x => x.Id).SingleAsync();
        var detail = await new GetDirectCommissionStatementQueryHandler(db, user).Handle(new(foreign), default);
        Assert.Equal("commission.not_found", detail.ErrorCode);
    }

    [Fact]
    public async Task StaffCanFilterAdministrativeListAndTravelerCannotReadIt()
    {
        await using var db = Db();
        db.DirectCommissionStatements.AddRange(Statement(10, "2026-10"), Statement(11, "2026-09"));
        await db.SaveChangesAsync();
        var staff = new FakeCurrentUserService { UserId = 20, Role = "Staff" };
        var list = await new ListDirectCommissionStatementsQueryHandler(db, staff).Handle(new(AdminView: true, TourOperatorUserId: 11), default);
        Assert.Equal(11, Assert.Single(list.Value!.Items).TourOperatorUserId);
        var denied = await new ListDirectCommissionStatementsQueryHandler(db, new FakeCurrentUserService { UserId = 10, Role = "Traveler" }).Handle(new(), default);
        Assert.Equal("commission.forbidden", denied.ErrorCode);
    }

    [Theory]
    [InlineData(0, 20, null)]
    [InlineData(1, 101, null)]
    [InlineData(1, 20, "bad")]
    public async Task PaginationAndPeriodAreValidated(int page, int size, string? period)
    {
        await using var db = Db();
        var result = await new ListDirectCommissionStatementsQueryHandler(db, new FakeCurrentUserService { UserId = 10, Role = "TourOperator" })
            .Handle(new(PageNumber: page, PageSize: size, Period: period), default);
        Assert.True(result.IsFailure);
    }

    private static DirectCommissionStatement Statement(long owner, string period) => DirectCommissionStatement.Issue($"DCO-{owner}-{period}", owner, period, 100000, .1m, 10000, new(2026, 11, 1), new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    private static Context Db() => new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed class Context(DbContextOptions<TestDbContext> options) : TestDbContext(options), IApplicationDbContext
    {
    }
}