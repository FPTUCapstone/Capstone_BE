using System.Reflection;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.Coupons.Common;
using TripMate.Application.Features.Coupons.Lifecycle;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Coupons.Lifecycle;

public sealed class SetCouponActivationCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_ActivateWhenApplicableTourIsNoLongerPublic_ReturnsIneligibleAndStaysDisabled()
    {
        await using var db = TestDbContext.Create();
        var operatorUser = new User
        {
            Id = 7,
            Email = "operator@test.local",
            FullName = "Operator",
            Role = UserRole.TourOperator,
            Status = AccountStatus.Active,
        };
        db.Users.Add(operatorUser);
        db.OperatorProfiles.Add(new OperatorProfile
        {
            UserId = operatorUser.Id,
            User = operatorUser,
            CompanyName = "Operator",
            TaxCode = "TAX7",
            BusinessLicenseNo = "LICENSE7",
            ApprovalStatus = OperatorApprovalStatus.Approved,
        });
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        Set(tour, nameof(Tour.OperatorUserId), operatorUser.Id);
        Set(tour, nameof(Tour.Title), "Tour");
        Set(tour, nameof(Tour.BasePrice), 1_000_000m);
        Set(tour, nameof(Tour.DurationDays), 1);
        Set(tour, nameof(Tour.Status), TourStatus.Published);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        var coupon = Voucher.Create(operatorUser.Id, "SAVE10", "Coupon", VoucherDiscountType.Percentage,
            10m, 200_000m, 0m, 5, 1, Now.AddDays(-1), Now.AddDays(2), [tour], Now.AddDays(-2));
        coupon.Deactivate(Now.AddHours(-1));
        db.Vouchers.Add(coupon);
        await db.SaveChangesAsync();
        Set(tour, nameof(Tour.Status), TourStatus.Draft);
        await db.SaveChangesAsync();
        db.ClearTrackedEntities();

        var handler = new SetCouponActivationCommandHandler(
            db,
            new FakeCurrentUserService { UserId = operatorUser.Id },
            new FakeDateTimeProvider { UtcNow = Now });

        var result = await handler.Handle(
            new SetCouponActivationCommand(coupon.Id, Activate: true), CancellationToken.None);

        result.ErrorCode.Should().Be(CouponErrorCodes.TourNotEligible);
        (await db.Vouchers.AsNoTracking().SingleAsync()).Status.Should().Be(VoucherStatus.Disabled);
    }

    private static void Set<T>(object instance, string property, T value) =>
        instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(instance, value);
}