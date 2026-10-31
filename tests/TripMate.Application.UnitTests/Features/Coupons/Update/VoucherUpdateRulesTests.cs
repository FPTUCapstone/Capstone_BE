using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Coupons.Update;

public sealed class VoucherUpdateRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Update_RejectsChangingDiscountTypeWithoutMutatingCoupon()
    {
        var voucher = CreateVoucher();

        Action update = () => Update(voucher, discountType: VoucherDiscountType.Flat,
            discountValue: 100_000m, maxDiscountAmount: null);

        update.Should().Throw<ArgumentException>();
        voucher.DiscountType.Should().Be(VoucherDiscountType.Percentage);
        voucher.DiscountValue.Should().Be(10m);
        voucher.Code.Should().Be("SUMMER10");
    }

    [Fact]
    public void Update_RejectsMovingAnElapsedStartWithoutMutatingCoupon()
    {
        var voucher = CreateVoucher();

        Action update = () => Update(voucher, validFromUtc: Now.AddHours(-1));

        update.Should().Throw<ArgumentException>();
        voucher.ValidFromUtc.Should().Be(Now.AddDays(-1));
        voucher.DiscountValue.Should().Be(10m);
    }

    [Fact]
    public void Update_AllowsMovingStartBeforeItBegins()
    {
        var voucher = CreateVoucher(validFromUtc: Now.AddDays(1), validToUtc: Now.AddDays(3));

        Update(voucher, validFromUtc: Now.AddDays(2), validToUtc: Now.AddDays(4));

        voucher.ValidFromUtc.Should().Be(Now.AddDays(2));
    }

    [Fact]
    public void Update_ExtendingAnExpiredActiveCouponRequiresSeparateActivation()
    {
        var voucher = CreateVoucher(validToUtc: Now.AddHours(-1));

        Update(voucher, validToUtc: Now.AddDays(2));

        voucher.Status.Should().Be(VoucherStatus.Disabled);
        voucher.Activate(Now);
        voucher.Status.Should().Be(VoucherStatus.Active);
    }

    [Fact]
    public void Update_ManuallyDisabledCouponStaysDisabled()
    {
        var voucher = CreateVoucher();
        voucher.Deactivate(Now.AddMinutes(-1));

        Update(voucher, discountValue: 20m);

        voucher.Status.Should().Be(VoucherStatus.Disabled);
    }

    [Fact]
    public void Update_LimitEqualToUsedCountIsValidAndNullMeansUnlimited()
    {
        var voucher = CreateVoucher();
        voucher.IncrementUsage();

        Update(voucher, usageLimit: 1, usageLimitPerUser: null);
        voucher.UsageLimit.Should().Be(1);
        voucher.UsageLimitPerUser.Should().BeNull();
        voucher.UsedCount.Should().Be(1);
        ((Action)voucher.IncrementUsage).Should().Throw<InvalidOperationException>();

        Update(voucher, usageLimit: null);
        voucher.UsageLimit.Should().BeNull();
        voucher.UsedCount.Should().Be(1);
    }

    [Fact]
    public void Update_RejectsLimitBelowUsedCountWithoutMutatingCoupon()
    {
        var voucher = CreateVoucher();
        voucher.IncrementUsage();
        voucher.IncrementUsage();

        Action update = () => Update(voucher, usageLimit: 1, discountValue: 20m);

        update.Should().Throw<ArgumentOutOfRangeException>();
        voucher.UsedCount.Should().Be(2);
        voucher.DiscountValue.Should().Be(10m);
    }

    [Fact]
    public void Update_ChangesValueWithinTypeAndRetainsCodeAndUsage()
    {
        var voucher = CreateVoucher();
        voucher.IncrementUsage();

        Update(voucher, discountValue: 20m);

        voucher.Code.Should().Be("SUMMER10");
        voucher.DiscountType.Should().Be(VoucherDiscountType.Percentage);
        voucher.DiscountValue.Should().Be(20m);
        voucher.UsedCount.Should().Be(1);
        voucher.Status.Should().Be(VoucherStatus.Active);
    }

    [Fact]
    public void ReleaseUsage_DecrementsOnceAndRejectsNegativeCount()
    {
        var voucher = CreateVoucher();
        voucher.IncrementUsage();

        voucher.ReleaseUsage();

        voucher.UsedCount.Should().Be(0);
        ((Action)voucher.ReleaseUsage).Should().Throw<InvalidOperationException>();
    }

    private static Voucher CreateVoucher(DateTimeOffset? validFromUtc = null, DateTimeOffset? validToUtc = null)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        tour.Id = 1;
        return Voucher.Create(7, "SUMMER10", "Summer coupon", VoucherDiscountType.Percentage,
            10m, 200_000m, 0m, 5, 1, validFromUtc ?? Now.AddDays(-1),
            validToUtc ?? Now.AddDays(1), [tour], Now.AddDays(-3));
    }

    private static void Update(Voucher voucher, VoucherDiscountType discountType = VoucherDiscountType.Percentage,
        decimal discountValue = 10m, decimal? maxDiscountAmount = 200_000m,
        int? usageLimit = 5, int? usageLimitPerUser = 1,
        DateTimeOffset? validFromUtc = null, DateTimeOffset? validToUtc = null)
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        tour.Id = 1;
        voucher.Update("Updated coupon", discountType, discountValue, maxDiscountAmount, 0m,
            usageLimit, usageLimitPerUser, validFromUtc ?? voucher.ValidFromUtc,
            validToUtc ?? Now.AddDays(3), [tour], Now);
    }
}