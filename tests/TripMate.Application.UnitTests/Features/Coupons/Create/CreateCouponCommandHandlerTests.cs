using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Coupons.Common;
using TripMate.Application.Features.Coupons.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Coupons.Create;

public sealed class CreateCouponCommandHandlerTests
{
    [Fact]
    public async Task Handle_ForApprovedOwner_CreatesVoucherAndTourLinks()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 7);
        var tour = SeedTour(actor.Id, TourStatus.Approved);
        db.Tours.Add(tour); await db.SaveChangesAsync();
        var handler = new CreateCouponCommandHandler(db, new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(tour.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("SUMMER10");
        (await db.Vouchers.Include(v => v.ApplicableTours).SingleAsync()).ApplicableTours.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_ForForeignTour_ReturnsForbiddenWithoutVoucher()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 7); var other = await SeedOperatorAsync(db, 8);
        var tour = SeedTour(other.Id, TourStatus.Approved); db.Tours.Add(tour); await db.SaveChangesAsync();
        var handler = new CreateCouponCommandHandler(db, new FakeCurrentUserService { UserId = actor.Id }, new FakeDateTimeProvider());

        var result = await handler.Handle(Command(tour.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(CouponErrorCodes.TourNotOwned);
        (await db.Vouchers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenApplicableToursAreMissing_ReturnsValidationFailureInsteadOfThrowing()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 7);
        var handler = new CreateCouponCommandHandler(
            db,
            new FakeCurrentUserService { UserId = actor.Id },
            new FakeDateTimeProvider());

        var request = Command(1) with { ApplicableTourIds = null };
        var result = await handler.Handle(request, CancellationToken.None);

        result.ErrorCode.Should().Be(CouponErrorCodes.InvalidScope);
        (await db.Vouchers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_ForOwnUnapprovedTour_ReturnsIneligibleFailure()
    {
        await using var db = TestDbContext.Create();
        var actor = await SeedOperatorAsync(db, 7);
        var tour = SeedTour(actor.Id, TourStatus.Draft);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        var handler = new CreateCouponCommandHandler(
            db,
            new FakeCurrentUserService { UserId = actor.Id },
            new FakeDateTimeProvider());

        var result = await handler.Handle(Command(tour.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(CouponErrorCodes.TourNotEligible);
        (await db.Vouchers.CountAsync()).Should().Be(0);
    }

    private static CreateCouponCommand Command(long tourId) => new("summer10", VoucherDiscountType.Percentage, 10m, 200_000m, 0m, 5, 1,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), [tourId]);
    private static async Task<User> SeedOperatorAsync(TestDbContext db, long id)
    {
        var user = new User { Id = id, Email = $"{id}@test.local", FullName = "Operator", Role = UserRole.TourOperator, Status = AccountStatus.Active };
        db.Users.Add(user); db.OperatorProfiles.Add(new OperatorProfile { UserId = id, User = user, CompanyName = $"Co{id}", TaxCode = $"Tax{id}", BusinessLicenseNo = $"Lic{id}", ApprovalStatus = OperatorApprovalStatus.Approved }); await db.SaveChangesAsync(); return user;
    }
    private static Tour SeedTour(long operatorId, TourStatus status)
    { var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!; Set(tour, nameof(Tour.OperatorUserId), operatorId); Set(tour, nameof(Tour.Title), "Tour"); Set(tour, nameof(Tour.BasePrice), 1m); Set(tour, nameof(Tour.DurationDays), 1); Set(tour, nameof(Tour.Status), status); return tour; }
    private static void Set<T>(object instance, string property, T value) => instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(instance, value);
}