using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Features.Authentication.ChangePassword;
using TripMate.Application.Features.Authentication.Common;

using Xunit;

namespace TripMate.Application.UnitTests.Features.Authentication.ChangePassword;

public class ChangePasswordCommandValidatorTests
{
    private readonly ChangePasswordCommandValidator _validator = new();

    [Fact]
    public void Validate_WithEmptyCurrentPassword_FailsWithMsg01()
    {
        var result = _validator.TestValidate(new ChangePasswordCommand("", "NewPass2@", 1));

        result.ShouldHaveValidationErrorFor(x => x.CurrentPassword)
            .WithErrorCode(AuthErrorCodes.Msg01);
    }

    [Fact]
    public void Validate_WithEmptyNewPassword_FailsWithMsg01()
    {
        var result = _validator.TestValidate(new ChangePasswordCommand("CurrentPass1!", "", 1));

        result.ShouldHaveValidationErrorFor(x => x.NewPassword)
            .WithErrorCode(AuthErrorCodes.Msg01);
    }

    [Theory]
    [InlineData("short1!")]
    [InlineData("nouppercase1!")]
    [InlineData("NOLOWERCASE1!")]
    [InlineData("NoDigitHere!")]
    [InlineData("NoSpecialChar1")]
    [InlineData("Has Whitespace1!")]
    public void Validate_WithNewPasswordViolatingPolicy_FailsWithMsg05(string newPassword)
    {
        var result = _validator.TestValidate(new ChangePasswordCommand("CurrentPass1!", newPassword, 1));

        result.ShouldHaveValidationErrorFor(x => x.NewPassword)
            .WithErrorCode(AuthErrorCodes.Msg05);
    }

    [Fact]
    public void Validate_WithValidDistinctValues_Passes()
    {
        var result = _validator.TestValidate(new ChangePasswordCommand("CurrentPass1!", "NewPass2@", 1));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_DoesNotRejectNewPasswordEqualToCurrent_HandlerOwnsThatCheck()
    {
        // The validator has no cross-field access to the stored hash; the authoritative
        // distinct-password check lives in ChangePasswordCommandHandler (spec §3.5).
        var result = _validator.TestValidate(new ChangePasswordCommand("SamePass1!", "SamePass1!", 1));

        result.ShouldNotHaveAnyValidationErrors();
    }
}