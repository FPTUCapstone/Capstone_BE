using FluentAssertions;

using TripMate.Application.Features.Coupons.Update;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Coupons.Update;

public sealed class UpdateCouponCommandValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    private readonly UpdateCouponCommandValidator validator = new(new FakeDateTimeProvider { UtcNow = Now });

    [Fact]
    public void Validate_AllowsElapsedStartAndUnlimitedLimitsForAnExtension()
    {
        var command = ValidCommand() with
        {
            ValidFromUtc = Now.AddDays(-7),
            UsageLimit = null,
            UsageLimitPerUser = null,
        };

        validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_RejectsMissingNameWithoutThrowing()
    {
        var command = ValidCommand() with { Name = null! };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(UpdateCouponCommand.Name));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsNonPositiveOptionalLimits(int limit)
    {
        var command = ValidCommand() with { UsageLimit = limit, UsageLimitPerUser = limit };

        validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsAnEndThatIsNotInTheFuture()
    {
        var command = ValidCommand() with { ValidToUtc = Now };

        validator.Validate(command).IsValid.Should().BeFalse();
    }

    private static UpdateCouponCommand ValidCommand() => new(1, "Summer coupon",
        VoucherDiscountType.Percentage, 10m, 200_000m, 0m, 5, 1,
        Now.AddDays(-1), Now.AddDays(1), [1]);
}