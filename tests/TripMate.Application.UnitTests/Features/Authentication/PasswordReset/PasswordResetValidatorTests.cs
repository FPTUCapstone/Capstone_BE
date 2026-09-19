using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Features.Authentication.Common;
using TripMate.Application.Features.Authentication.PasswordReset;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.PasswordReset;

public class PasswordResetValidatorTests
{
    private const string ValidEmail = "user@example.com";
    private const string ValidPassword = "Passw0rd!";

    private readonly RequestPasswordResetCommandValidator _requestValidator = new();
    private readonly ConfirmPasswordResetCommandValidator _confirmValidator = new();

    [Fact(DisplayName = "PLAN-VAL-01: email is required")]
    public void Request_WhenEmailEmpty_IsInvalid()
    {
        var result = _requestValidator.Validate(new RequestPasswordResetCommand(""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(f => f.ErrorCode == AuthErrorCodes.Msg01);
    }

    [Fact(DisplayName = "PLAN-VAL-02: invalid email format is rejected")]
    public void Request_WhenEmailMalformed_IsInvalid()
    {
        var result = _requestValidator.Validate(new RequestPasswordResetCommand("not-an-email"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(f => f.ErrorCode == AuthErrorCodes.Msg02);
    }

    [Fact(DisplayName = "PLAN-VAL-03: email longer than 254 characters is rejected")]
    public void Request_WhenEmailExceeds254Characters_IsInvalid()
    {
        var email = new string('a', 250) + "@example.com";

        var result = _requestValidator.Validate(new RequestPasswordResetCommand(email));

        result.IsValid.Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-VAL-04: code is required")]
    public void Confirm_WhenCodeEmpty_IsInvalid()
    {
        var result = _confirmValidator.Validate(new ConfirmPasswordResetCommand(
            ValidEmail, "", ValidPassword));

        result.IsValid.Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-VAL-05: code shorter than 6 characters is rejected")]
    public void Confirm_WhenCodeShorterThan6_IsInvalid()
    {
        var result = _confirmValidator.Validate(new ConfirmPasswordResetCommand(
            ValidEmail, "12345", ValidPassword));

        result.IsValid.Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-VAL-06: code longer than 6 characters is rejected")]
    public void Confirm_WhenCodeLongerThan6_IsInvalid()
    {
        var result = _confirmValidator.Validate(new ConfirmPasswordResetCommand(
            ValidEmail, "1234567", ValidPassword));

        result.IsValid.Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-VAL-07: non-numeric code is rejected")]
    public void Confirm_WhenCodeNonNumeric_IsInvalid()
    {
        var result = _confirmValidator.Validate(new ConfirmPasswordResetCommand(
            ValidEmail, "12345a", ValidPassword));

        result.IsValid.Should().BeFalse();
    }

    [Fact(DisplayName = "PLAN-VAL-08: leading-zero code 000001 is accepted")]
    public void Confirm_WhenCodeHasLeadingZeros_IsValid()
    {
        var result = _confirmValidator.Validate(new ConfirmPasswordResetCommand(
            ValidEmail, "000001", ValidPassword));

        result.IsValid.Should().BeTrue();
    }

    [Fact(DisplayName = "PLAN-VAL-09: newPassword follows the existing registration password policy")]
    public void Confirm_WhenNewPasswordViolatesRegistrationPolicy_IsInvalid()
    {
        var weakPassword = _confirmValidator.Validate(new ConfirmPasswordResetCommand(
            ValidEmail, "000001", "short"));
        var missingUppercase = _confirmValidator.Validate(new ConfirmPasswordResetCommand(
            ValidEmail, "000001", "password1!"));
        var missingSpecial = _confirmValidator.Validate(new ConfirmPasswordResetCommand(
            ValidEmail, "000001", "Password123"));

        weakPassword.IsValid.Should().BeFalse();
        weakPassword.Errors.Should().Contain(f => f.ErrorCode == AuthErrorCodes.Msg05);
        missingUppercase.IsValid.Should().BeFalse();
        missingUppercase.Errors.Should().Contain(f => f.ErrorCode == AuthErrorCodes.Msg05);
        missingSpecial.IsValid.Should().BeFalse();
        missingSpecial.Errors.Should().Contain(f => f.ErrorCode == AuthErrorCodes.Msg05);
    }
}