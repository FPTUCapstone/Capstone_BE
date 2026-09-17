using FluentAssertions;

using TripMate.Application.Features.TravelGroups.JoinTravelGroup;

namespace TripMate.Application.UnitTests.Features.TravelGroups.JoinTravelGroup;

public sealed class JoinTravelGroupCommandValidatorTests
{
    private readonly JoinTravelGroupCommandValidator _validator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenInvitationCodeIsEmpty_ReturnsMSG01(string? code)
    {
        var command = new JoinTravelGroupCommand(code!, 1, Guid.NewGuid());

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(command.InvitationCode)
            && e.ErrorMessage == "This field is required.");
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("ABC123456")]
    [InlineData("ABC-1234")]
    [InlineData("ABC 1234")]
    public void Validate_WhenInvitationCodeIsNot8AlphanumericChars_ReturnsValidationError(string code)
    {
        var command = new JoinTravelGroupCommand(code, 1, Guid.NewGuid());

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(command.InvitationCode)
            && e.ErrorMessage == "Invitation code must be 8 alphanumeric characters.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenTravelerUserIdIsNotPositive_ReturnsValidationError(long userId)
    {
        var command = new JoinTravelGroupCommand("A7K4P2QX", userId, Guid.NewGuid());

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(command.TravelerUserId));
    }

    [Fact]
    public void Validate_WhenIdempotencyKeyIsEmpty_ReturnsValidationError()
    {
        var command = new JoinTravelGroupCommand("A7K4P2QX", 1, Guid.Empty);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(command.IdempotencyKey));
    }

    [Fact]
    public void Validate_WhenValid_Passes()
    {
        var command = new JoinTravelGroupCommand("A7K4P2QX", 1, Guid.NewGuid());

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }
}
