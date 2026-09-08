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
        var command = new RegisterTravelerCommand(
            "jane@example.com",
            "Password123!",
            "Jane Traveler",
            "0912345678",
            true,
            "valid-token");

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Password123!", "Jane Traveler", "0912345678", true)] // Empty email
    [InlineData("invalid-email", "Password123!", "Jane Traveler", "0912345678", true)] // Invalid email format
    [InlineData("jane@example.com", "short", "Jane Traveler", "0912345678", true)] // Short password (< 8)
    [InlineData("jane@example.com", "password123!", "Jane Traveler", "0912345678", true)] // No uppercase
    [InlineData("jane@example.com", "PASSWORD123!", "Jane Traveler", "0912345678", true)] // No lowercase
    [InlineData("jane@example.com", "Password!", "Jane Traveler", "0912345678", true)] // No digit
    [InlineData("jane@example.com", "Password123", "Jane Traveler", "0912345678", true)] // No special char
    [InlineData("jane@example.com", "Password123!", "", "0912345678", true)] // Empty full name
    [InlineData("jane@example.com", "Password123!", "Jane Traveler", "12345", true)] // Invalid phone
    [InlineData("jane@example.com", "Password123!", "Jane Traveler", "0912345678", false)] // Terms not accepted
    public void Validate_WithInvalidCommand_HasErrors(
        string email,
        string password,
        string fullName,
        string phoneNumber,
        bool acceptedTerms)
    {
        var command = new RegisterTravelerCommand(email, password, fullName, phoneNumber, acceptedTerms, "valid-token");

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
    }
}
