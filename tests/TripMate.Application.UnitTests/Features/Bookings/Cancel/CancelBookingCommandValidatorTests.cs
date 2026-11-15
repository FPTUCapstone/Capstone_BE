using FluentAssertions;

using TripMate.Application.Features.Bookings.Cancel;

namespace TripMate.Application.UnitTests.Features.Bookings.Cancel;

public sealed class CancelBookingCommandValidatorTests
{
    private readonly CancelBookingCommandValidator validator = new();

    [Fact]
    public void Validate_RejectsReasonWhoseComposedValueExceedsBookingLimit()
    {
        var command = new CancelBookingCommand(
            1,
            Guid.NewGuid(),
            new string('C', CancelBookingCommandValidator.ReasonCategoryMaxLength),
            new string('D', CancelBookingCommandValidator.ReasonDetailMaxLength));

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("combined cancellation reason"));
    }

    [Fact]
    public void Validate_AcceptsReasonAtComposedBookingLimit()
    {
        var command = new CancelBookingCommand(
            1,
            Guid.NewGuid(),
            new string('C', CancelBookingCommandValidator.ReasonCategoryMaxLength),
            new string('D', 234));

        validator.Validate(command).IsValid.Should().BeTrue();
    }
}