using FluentAssertions;

using TripMate.Application.Features.TravelGroups.CreateTravelGroup;

using Xunit;

namespace TripMate.Application.UnitTests.Features.TravelGroups.CreateTravelGroup;

/**
 * [UC-17] Unit tests for CreateTravelGroupCommandValidator
 */
public class CreateTravelGroupCommandValidatorTests
{
    private readonly CreateTravelGroupCommandValidator _validator = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var command = new CreateTravelGroupCommand(1, "Da Nang Summer Trip", 10);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WithEmptyGroupName_HasError(string? groupName)
    {
        var command = new CreateTravelGroupCommand(1, groupName!, 10);


        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateTravelGroupCommand.GroupName));
    }

    [Fact]
    public void Validate_WithGroupNameExceeding150Chars_HasError()
    {
        var longName = new string('A', 151);
        var command = new CreateTravelGroupCommand(1, longName, 10);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateTravelGroupCommand.GroupName));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithInvalidItineraryId_HasError(long itineraryId)
    {
        var command = new CreateTravelGroupCommand(itineraryId, "Valid Name", 10);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateTravelGroupCommand.ItineraryId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithInvalidHostUserId_HasError(long hostUserId)
    {
        var command = new CreateTravelGroupCommand(1, "Valid Name", hostUserId);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateTravelGroupCommand.HostUserId));
    }
}