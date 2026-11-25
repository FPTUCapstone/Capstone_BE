using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Coupons.Common;
using TripMate.Application.Features.Coupons.Update;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Coupons.Update;

public sealed class UpdateCouponCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_UpdatesAnOwnedCouponInOneSerializableTransaction()
    {
        await using var db = TestDbContext.Create();
        var (operatorId, coupon, tour) = await SeedAsync(db);
        var handler = Handler(db, operatorId);

        var result = await handler.Handle(Command(coupon.Id, tour.Id) with { Name = "Renamed", DiscountValue = 20m }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Renamed");
        result.Value.DiscountValue.Should().Be(20m);
        result.Value.Code.Should().Be("SAVE10");
        db.TransactionExecutionCount.Should().Be(1);
    }

    [Fact]
    public async Task Handle_RetainingSameTourDoesNotDuplicateOrDeleteItsLink()
    {
        await using var db = TestDbContext.Create();
        var (operatorId, coupon, tour) = await SeedAsync(db);
        var handler = Handler(db, operatorId);

        var result = await handler.Handle(Command(coupon.Id, tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await db.Vouchers.Include(v => v.ApplicableTours).SingleAsync()).ApplicableTours
            .Should().ContainSingle(link => link.TourId == tour.Id);
    }

    [Fact]
    public async Task Handle_RejectsForeignCouponWithoutChangingIt()
    {
        await using var db = TestDbContext.Create();
        var (_, coupon, tour) = await SeedAsync(db);
        var foreignOperator = await SeedOperatorAsync(db, 8);
        var handler = Handler(db, foreignOperator.Id);

        var result = await handler.Handle(Command(coupon.Id, tour.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(CouponErrorCodes.NotFound);
        coupon.Name.Should().Be("Original");
    }

    [Fact]
    public async Task Handle_RejectsMixedOwnedAndForeignToursWithoutPartialChange()
    {
        await using var db = TestDbContext.Create();
        var (operatorId, coupon, tour) = await SeedAsync(db);
        var foreignOperator = await SeedOperatorAsync(db, 8);
        var foreignTour = SeedTour(foreignOperator.Id);
        db.Tours.Add(foreignTour);
        await db.SaveChangesAsync();
        var handler = Handler(db, operatorId);

        var result = await handler.Handle(Command(coupon.Id, tour.Id) with
        { Name = "Should not persist", ApplicableTourIds = [tour.Id, foreignTour.Id] }, CancellationToken.None);

        result.ErrorCode.Should().Be(CouponErrorCodes.TourNotOwned);
        coupon.Name.Should().Be("Original");
        coupon.ApplicableTours.Should().ContainSingle(link => link.TourId == tour.Id);
    }

    [Fact]
    public async Task Handle_LimitEqualToOccupiedCountSucceedsButLowerLimitConflicts()
    {
        await using var db = TestDbContext.Create();
        var (operatorId, coupon, tour) = await SeedAsync(db);
        coupon.IncrementUsage();
        coupon.IncrementUsage();
        await db.SaveChangesAsync();
        var handler = Handler(db, operatorId);

        var equal = await handler.Handle(Command(coupon.Id, tour.Id) with { UsageLimit = 2 }, CancellationToken.None);
        var below = await handler.Handle(Command(coupon.Id, tour.Id) with { UsageLimit = 1 }, CancellationToken.None);

        equal.IsSuccess.Should().BeTrue();
        below.ErrorCode.Should().Be(CouponErrorCodes.UsageConflict);
        coupon.UsageLimit.Should().Be(2);
        coupon.UsedCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_MapsAConcurrentSaveToUsageConflictAndClearsTrackedState()
    {
        await using var db = TestDbContext.Create();
        var (operatorId, coupon, tour) = await SeedAsync(db);
        var handler = Handler(db, operatorId);
        db.ThrowOnSaveConcurrency = true;

        var result = await handler.Handle(Command(coupon.Id, tour.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(CouponErrorCodes.UsageConflict);
        db.ChangeTracker.Entries().Should().BeEmpty();
    }

    private static UpdateCouponCommandHandler Handler(TestDbContext db, long operatorId) =>
        new(db, new FakeCurrentUserService { UserId = operatorId }, new FakeDateTimeProvider { UtcNow = Now });

    private static UpdateCouponCommand Command(long couponId, long tourId) =>
        new(couponId, "Updated", VoucherDiscountType.Percentage, 10m, 200_000m, 0m,
            5, 1, Now.AddDays(-1), Now.AddDays(2), [tourId]);

    private static async Task<(long OperatorId, Voucher Coupon, Tour Tour)> SeedAsync(TestDbContext db)
    {
        var operatorUser = await SeedOperatorAsync(db, 7);
        var tour = SeedTour(operatorUser.Id);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var coupon = Voucher.Create(operatorUser.Id, "SAVE10", "Original", VoucherDiscountType.Percentage,
            10m, 200_000m, 0m, 5, 1, Now.AddDays(-1), Now.AddDays(2), [tour], Now.AddDays(-7));
        db.Vouchers.Add(coupon);
        await db.SaveChangesAsync();
        return (operatorUser.Id, coupon, tour);
    }

    private static async Task<User> SeedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User
        {
            Id = id,
            Email = $"operator-{id}@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(user);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = id,
            User = user,
            CompanyName = $"Operator {id}",
            TaxCode = $"TAX{id}",
            BusinessLicenseNo = $"LICENSE{id}",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        await db.SaveChangesAsync();
        return user;
    }

    private static Tour SeedTour(long operatorId)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorId);
        Set(tour, nameof(Tour.Title), "Tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        return tour;
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}