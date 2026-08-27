using FluentAssertions;
using TripMate.Application.Features.Authentication.Login;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.Login;

public class LoginCommandValidatorTests
{
    private readonly LoginCommandValidator _validator = new();

    [Fact]
    public void Validate_WithValidCommand_HasNoErrors()
    {
        var result = _validator.Validate(new LoginCommand("user@example.com", "Passw0rd123"));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Passw0rd123")]
    [InlineData("not-an-email", "Passw0rd123")]
    [InlineData("user@example.com", "")]
    public void Validate_WithInvalidCommand_HasErrors(string email, string password)
    {
        var result = _validator.Validate(new LoginCommand(email, password));

        result.IsValid.Should().BeFalse();
    }
}
