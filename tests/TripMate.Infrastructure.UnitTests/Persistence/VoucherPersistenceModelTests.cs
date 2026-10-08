using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Infrastructure.UnitTests.Persistence;

public sealed class VoucherPersistenceModelTests
{
    [Fact]
    public void Voucher_CreatePercentage_UsesActiveStatusAndConfiguredCap()
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;

        var voucher = Voucher.Create(
            7,
            "summer10",
            VoucherDiscountType.Percentage,
            10m,
            200_000m,
            1_000_000m,
            100,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7),
            [tour],
            DateTimeOffset.UtcNow);

        voucher.Code.Should().Be("SUMMER10");
        voucher.Status.Should().Be(VoucherStatus.Active);
        voucher.UsedCount.Should().Be(0);
        voucher.MaxDiscountAmount.Should().Be(200_000m);
    }

    [Fact]
    public void Voucher_Create_RejectsInvalidCodeAndDiscountType()
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        var create = () => Voucher.Create(7, new string('A', Voucher.CodeMaxLength + 1),
            (VoucherDiscountType)99, 10m, 200_000m, 0m, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), [tour], DateTimeOffset.UtcNow);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Voucher_Create_RejectsCodesShorterThanThreeCharactersAndExpiredWindows()
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        var now = DateTimeOffset.UtcNow;
        var shortCode = () => Voucher.Create(7, "AA", VoucherDiscountType.Percentage,
            10m, 200_000m, 0m, null, null,
            now, now.AddDays(1), [tour], now);
        var expiredWindow = () => Voucher.Create(7, "TRIP10", VoucherDiscountType.Percentage,
            10m, 200_000m, 0m, null, null,
            now.AddDays(-2), now.AddDays(-1), [tour], now);

        shortCode.Should().Throw<ArgumentException>();
        expiredWindow.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Voucher_Create_RejectsDuplicateTours()
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        var create = () => Voucher.Create(7, "SUMMER10", VoucherDiscountType.Percentage,
            10m, 200_000m, 0m, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), [tour, tour], DateTimeOffset.UtcNow);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Voucher_Create_RejectsMoneyThatWouldBeRoundedOrAFlatDiscountAboveMinimumOrder()
    {
        var tour = (Tour)Activator.CreateInstance(typeof(Tour), nonPublic: true)!;
        var roundedMoney = () => Voucher.Create(7, "TRIP10", VoucherDiscountType.Percentage,
            10.001m, 200_000m, 0m, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), [tour], DateTimeOffset.UtcNow);
        var excessiveFlatDiscount = () => Voucher.Create(7, "TRIP20", VoucherDiscountType.Flat,
            200_000m, null, 100_000m, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), [tour], DateTimeOffset.UtcNow);

        roundedMoney.Should().Throw<ArgumentOutOfRangeException>();
        excessiveFlatDiscount.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Model_MapsVoucherCodeToCommerceVouchersWithUniqueIndex()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ApplicationDbContext(options);

        var entity = db.Model.FindEntityType(typeof(Voucher));

        entity.Should().NotBeNull();
        entity!.GetTableName().Should().Be("Vouchers");
        entity.GetSchema().Should().Be("commerce");
        entity.GetIndexes().Single(index => index.Properties.Single().Name == nameof(Voucher.Code))
            .IsUnique.Should().BeTrue();

        var link = db.Model.FindEntityType(typeof(VoucherApplicableTour));
        link!.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(VoucherApplicableTour.VoucherId), nameof(VoucherApplicableTour.TourId));
        link.GetForeignKeys().Should().HaveCount(2);
    }
}