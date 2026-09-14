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

    [Fact]
    public void Validate_WithCoEmail_HasNoErrors()
    {
        var command = new RegisterTravelerCommand(
            "bathinh2k4@gmail.co",
            "Password123!",
            "Jane Traveler",
            null,
            true,
            "valid-token");

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmailLength254_HasNoErrors()
    {
        var email = $"{new string('a', 243)}@example.co";

        email.Length.Should().Be(254);
        _validator.Validate(new RegisterTravelerCommand(
            email,
            "Password123!",
            "Jane Traveler",
            null,
            true,
            "valid-token")).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmailLength255_HasErrors()
    {
        var email = $"{new string('a', 244)}@example.co";

        email.Length.Should().Be(255);
        _validator.Validate(new RegisterTravelerCommand(
            email,
            "Password123!",
            "Jane Traveler",
            null,
            true,
            "valid-token")).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(72, true)]
    [InlineData(73, false)]
    public void Validate_WithPasswordLengthBoundary_MatchesContract(int length, bool expectedIsValid)
    {
        var password = "Aa1!" + new string('a', length - 4);

        _validator.Validate(new RegisterTravelerCommand(
            "jane@example.com",
            password,
            "Jane Traveler",
            null,
            true,
            "valid-token")).IsValid.Should().Be(expectedIsValid);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Jane2")]
    [InlineData("Jane-Doe")]
    [InlineData("Jane\tDoe")]
    [InlineData("Jane\nDoe")]
    public void Validate_WithInvalidFullName_HasErrors(string fullName)
    {
        var result = _validator.Validate(new RegisterTravelerCommand(
            "jane@example.com",
            "Password123!",
            fullName,
            null,
            true,
            "valid-token"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithUnicodeFullName_HasNoErrors()
    {
        var result = _validator.Validate(new RegisterTravelerCommand(
            "jane@example.com",
            "Password123!",
            "Nguyễn Ánh",
            null,
            true,
            "valid-token"));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithOptionalPhone_HasNoErrors()
    {
        _validator.Validate(new RegisterTravelerCommand(
            "jane@example.com",
            "Password123!",
            "Jane Traveler",
            null,
            true,
            "valid-token")).IsValid.Should().BeTrue();
    }
}
