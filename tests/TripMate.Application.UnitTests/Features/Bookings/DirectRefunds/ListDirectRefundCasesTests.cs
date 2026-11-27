using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Bookings.DirectRefunds.Common;
using TripMate.Application.Features.Bookings.DirectRefunds.Get;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Bookings.DirectRefunds;

public sealed class ListDirectRefundCasesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AdminGetsActiveAndPreviouslyResolvedDisputesButNotUnrelatedRefunds()
    {
        await using var db = CreateDb();
        var active = await Seed(db, 10, disputed: true);
        var resolved = await Seed(db, 20, disputed: true);
        resolved.ResolveDisputeCompleted("Verified", Now);
        await Seed(db, 10);
        await db.SaveChangesAsync();
        var result = await Handler(db, 1, nameof(UserRole.Administrator)).Handle(new(), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.TotalCount);
        Assert.Equal(new[] { resolved.Id, active.Id }, result.Value.Items.Select(item => item.CaseId));
    }

    [Fact]
    public async Task OperatorGetsOnlyOwnRefundsAndCannotSeeAnotherOperatorsDispute()
    {
        await using var db = CreateDb();
        var own = await Seed(db, 10);
        await Seed(db, 20, disputed: true);
        var result = await Handler(db, 10, nameof(UserRole.TourOperator)).Handle(new(), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(own.Id, Assert.Single(result.Value!.Items).CaseId);
        Assert.Equal(1, result.Value.TotalCount);
    }

    [Fact]
    public async Task FilteringAndPaginationCountBeforePageAndUseIdToBreakTimestampTies()
    {
        await using var db = CreateDb();
        var first = await Seed(db, 10, disputed: true);
        await Seed(db, 10, disputed: true);
        await Seed(db, 10);
        var result = await Handler(db, 10, nameof(UserRole.TourOperator))
            .Handle(new(DirectRefundCase.StatusDisputed, 2, 1), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.TotalCount);
        Assert.Equal(first.Id, Assert.Single(result.Value.Items).CaseId);
        Assert.Equal(2, result.Value.PageNumber);
        Assert.Equal(1, result.Value.PageSize);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1L, "Staff")]
    [InlineData(1L, "Traveler")]
    public async Task OtherCallersAreRejected(long? id, string? role)
    {
        await using var db = CreateDb();
        var result = await Handler(db, id, role).Handle(new(), default);
        Assert.Equal(DirectRefundErrorCodes.AccessDenied, result.ErrorCode);
    }

    [Theory]
    [InlineData("anything", 1, 20)]
    [InlineData(null, 0, 20)]
    [InlineData(null, 1, 51)]
    [InlineData(null, int.MaxValue, 50)]
    public async Task InvalidInputsFailWithoutQuerying(string? status, int page, int size)
    {
        await using var db = CreateDb();
        var result = await Handler(db, 1, nameof(UserRole.Administrator)).Handle(new(status, page, size), default);
        Assert.Equal(DirectRefundErrorCodes.ValidationFailed, result.ErrorCode);
    }

    private static ListDirectRefundCasesQueryHandler Handler(RefundDb db, long? id, string? role) =>
        new(db, new FakeCurrentUserService { UserId = id, Role = role });

    private static async Task<DirectRefundCase> Seed(RefundDb db, long operatorId, bool disputed = false)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), true)!;
        typeof(Tour).GetProperty(nameof(Tour.OperatorUserId))!.SetValue(tour, operatorId);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var schedule = TourSchedule.Create(Now.AddDays(1), Now.AddDays(2), "Meeting", 10, Now);
        typeof(TourSchedule).GetProperty(nameof(TourSchedule.TourId))!.SetValue(schedule, tour.Id);
        db.TourSchedules.Add(schedule);
        await db.SaveChangesAsync();
        var booking = Booking.Create(100, schedule.Id, 1, 10000, BookingStatus.Confirmed, PaymentStatus.Paid);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var refund = DirectRefundCase.Create(booking.Id, DirectRefundCase.SourcePolicyCancellation, 10000, 10000, Now);
        if (disputed)
        {
            refund.ConfirmRecipient("VCB", "1234", "Traveler", Now);
            refund.MarkTransferReported(Now);
            refund.Dispute(Now);
        }
        db.Set<DirectRefundCase>().Add(refund);
        await db.SaveChangesAsync();
        return refund;
    }

    private static RefundDb CreateDb() => new(new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed class RefundDb(DbContextOptions<TestDbContext> options) : TestDbContext(options), IApplicationDbContext
    {
        DbSet<DirectRefundCase> IApplicationDbContext.DirectRefundCases => Set<DirectRefundCase>();
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<DirectRefundCase>();
        }
    }
}