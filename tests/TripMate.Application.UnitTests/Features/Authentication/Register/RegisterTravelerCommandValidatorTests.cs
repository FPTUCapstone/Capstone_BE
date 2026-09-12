using FluentAssertions;

using TripMate.Application.Features.Authentication.Register;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.Register;

public class RegisterTravelerCommandValidatorTests
{
    private readonly RegisterTravelerCommandValidator _validator = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var command = new RegisterTravelerCommand("traveler@example.com", "Passw0rd123", "Jane Traveler");

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Passw0rd123", "Jane")]
    [InlineData("not-an-email", "Passw0rd123", "Jane")]
    [InlineData("traveler@example.com", "short1A", "Jane")]
    [InlineData("traveler@example.com", "alllowercase1", "Jane")]
    [InlineData("traveler@example.com", "Passw0rd123", "")]
    public void Validate_WithInvalidCommand_HasErrors(string email, string password, string fullName)
    {
        var command = new RegisterTravelerCommand(email, password, fullName);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
    }
}