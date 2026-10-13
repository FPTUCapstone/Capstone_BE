using FluentAssertions;

using TripMate.Application.Features.Coupons.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Coupons.Create;

public sealed class CreateCouponCommandValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private readonly CreateCouponCommandValidator validator = new(
        new FakeDateTimeProvider { UtcNow = Now });

    [Fact]
    public void Validate_AcceptsPercentageCouponForDistinctTours()
    {
        var result = validator.Validate(ValidCommand());
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("bad code")]
    [InlineData("AA")]
    public void Validate_RejectsInvalidCode(string code)
    {
        var result = validator.Validate(ValidCommand(code: code));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsDuplicateApplicableTourIds()
    {
        var result = validator.Validate(ValidCommand(tourIds: [1, 1]));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsPercentageCouponWithoutCap()
    {
        var result = validator.Validate(ValidCommand(maxDiscountAmount: null));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsFlatCouponWithPercentageCap()
    {
        var command = ValidCommand() with
        {
            DiscountType = VoucherDiscountType.Flat,
            DiscountValue = 100_000m,
            MaxDiscountAmount = 1m,
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsMissingApplicableTours()
    {
        var result = validator.Validate(ValidCommand(tourIds: []));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsCouponWhoseValidityWindowHasAlreadyEnded()
    {
        var result = validator.Validate(ValidCommand(
            validFromUtc: Now.AddDays(-2),
            validToUtc: Now.AddDays(-1)));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(10.001)]
    [InlineData(10_000_000_000d)]
    public void Validate_RejectsMoneyThatCannotBeStoredExactly(decimal discountValue)
    {
        var result = validator.Validate(ValidCommand(discountValue: discountValue));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsFlatDiscountGreaterThanTheMinimumOrder()
    {
        var result = validator.Validate(ValidCommand(
            discountType: VoucherDiscountType.Flat,
            discountValue: 200_000m,
            maxDiscountAmount: null,
            minOrderAmount: 100_000m));

        result.IsValid.Should().BeFalse();
    }

    private static CreateCouponCommand ValidCommand(string code = "SUMMER10", decimal? maxDiscountAmount = 200_000m,
        IReadOnlyCollection<long>? tourIds = null, VoucherDiscountType discountType = VoucherDiscountType.Percentage,
        decimal discountValue = 10m, decimal minOrderAmount = 1_000_000m,
        DateTimeOffset? validFromUtc = null, DateTimeOffset? validToUtc = null) => new(
        code, discountType, discountValue, maxDiscountAmount, minOrderAmount, 100, 1,
        validFromUtc ?? Now,
        validToUtc ?? Now.AddDays(7),
        tourIds ?? [1, 2]);
}