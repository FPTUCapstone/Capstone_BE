using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Coupons.Update;

public sealed class VoucherUsageReleaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReleaseUsage_ReturnsAnOccupiedSlotWithoutChangingCouponTerms()
    {
        var voucher = CreateVoucher();
        voucher.IncrementUsage();

        voucher.ReleaseUsage();

        voucher.UsedCount.Should().Be(0);
        voucher.Code.Should().Be("SAVE10");
        voucher.DiscountValue.Should().Be(10m);
    }

    [Fact]
    public void ReleaseUsage_RejectsUnderflow()
    {
        var voucher = CreateVoucher();

        Action release = voucher.ReleaseUsage;

        release.Should().Throw<InvalidOperationException>();
        voucher.UsedCount.Should().Be(0);
    }

    private static Voucher CreateVoucher()
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        tour.Id = 1;
        return Voucher.Create(1, "SAVE10", VoucherDiscountType.Percentage, 10m, 500_000m,
            0m, 1, 1, Now.AddDays(-1), Now.AddDays(1), [tour], Now.AddDays(-2));
    }
}