using FluentAssertions;

using FluentValidation.Results;

using TripMate.Application.Features.Operator.Profile.Common;
using TripMate.Application.Features.Operator.Profile.Update;

namespace TripMate.Application.UnitTests.Features.Operator.Profile;

public sealed class UpdateOperatorProfileCommandValidatorTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0];
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 0];
    private static readonly byte[] Webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 0];

    private readonly UpdateOperatorProfileCommandValidator validator = new();

    [Fact]
    public void MinimalRequest_WithOnlyBusinessName_IsValid()
    {
        Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void FullRequest_IsValid()
    {
        var command = Valid() with
        {
            BusinessDescription = "About us",
            BusinessAddress = "1 Beach Rd",
            ContactPhone = "0901234567",
            ContactEmail = "hello@suntours.vn",
            Website = "https://suntours.vn/about",
            LogoBytes = Png,
            LogoContentType = "image/png",
        };

        Validate(command).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BusinessName_IsRequired_Msg01(string name)
    {
        AssertSingle(Valid() with { BusinessName = name }, "BusinessName", OperatorProfileErrorCodes.Required);
    }

    [Fact]
    public void BusinessName_LengthIsCheckedAfterTrim()
    {
        Validate(Valid() with { BusinessName = " " + new string('a', 200) + " " }).IsValid.Should().BeTrue();
        AssertSingle(Valid() with { BusinessName = new string('a', 201) }, "BusinessName",
            OperatorProfileErrorCodes.RequestInvalid);
    }

    [Fact]
    public void Description_AndAddress_LengthLimits()
    {
        AssertSingle(Valid() with { BusinessDescription = new string('a', 2001) }, "BusinessDescription",
            OperatorProfileErrorCodes.RequestInvalid);
        AssertSingle(Valid() with { BusinessAddress = new string('a', 301) }, "BusinessAddress",
            OperatorProfileErrorCodes.RequestInvalid);
        Validate(Valid() with { BusinessDescription = new string('a', 2000), BusinessAddress = new string('a', 300) })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("0901234567", true)]
    [InlineData(" 0901234567 ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("901234567", false)]
    [InlineData("090123456", false)]
    [InlineData("09012345678", false)]
    [InlineData("09012a4567", false)]
    [InlineData("0901234567\n", true)]
    public void ContactPhone_MustBeTenDigitsStartingWithZero_Msg04(string? phone, bool valid)
    {
        var result = Validate(Valid() with { ContactPhone = phone });

        result.IsValid.Should().Be(valid);
        if (!valid)
        {
            result.Errors.Should().ContainSingle()
                .Which.Should().Match<ValidationFailure>(error =>
                    error.PropertyName == "ContactPhone" && error.ErrorCode == OperatorProfileErrorCodes.InvalidPhone);
        }
    }

    [Theory]
    [InlineData("hello@suntours.vn", true)]
    [InlineData(" hello@suntours.vn ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("not-an-email", false)]
    [InlineData("a@", false)]
    public void ContactEmail_MustBeValidWhenPresent_Msg02(string? email, bool valid)
    {
        var result = Validate(Valid() with { ContactEmail = email });

        result.IsValid.Should().Be(valid);
        if (!valid)
        {
            result.Errors.Should().ContainSingle()
                .Which.Should().Match<ValidationFailure>(error =>
                    error.PropertyName == "ContactEmail" && error.ErrorCode == OperatorProfileErrorCodes.InvalidEmail);
        }
    }

    [Fact]
    public void ContactEmail_LongerThanTheColumn_IsRequestInvalid()
    {
        var email = new string('a', 195) + "@x.vn"; // 200 chars is allowed, 201 is not.
        Validate(Valid() with { ContactEmail = email }).IsValid.Should().BeTrue();
        AssertSingle(Valid() with { ContactEmail = "a" + email }, "ContactEmail", OperatorProfileErrorCodes.RequestInvalid);
    }

    [Theory]
    [InlineData("https://suntours.vn", true)]
    [InlineData("http://suntours.vn/a?b=1", true)]
    [InlineData(" https://suntours.vn ", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("suntours.vn", false)]
    [InlineData("ftp://suntours.vn", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative/path", false)]
    [InlineData("https://", false)]
    public void Website_MustBeAbsoluteHttpOrHttps(string? website, bool valid)
    {
        var result = Validate(Valid() with { Website = website });

        result.IsValid.Should().Be(valid);
        if (!valid)
        {
            result.Errors.Should().ContainSingle()
                .Which.Should().Match<ValidationFailure>(error =>
                    error.PropertyName == "Website" && error.ErrorCode == OperatorProfileErrorCodes.RequestInvalid);
        }
    }

    [Fact]
    public void Website_LongerThan300_IsRequestInvalid()
    {
        var longUrl = "https://x.vn/" + new string('a', 288); // 301 chars
        AssertSingle(Valid() with { Website = longUrl }, "Website", OperatorProfileErrorCodes.RequestInvalid);
        Validate(Valid() with { Website = longUrl[..300] }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public void Logo_AcceptsPngJpegWebpWithMatchingSignature(string contentType)
    {
        byte[] bytes = contentType switch { "image/png" => Png, "image/jpeg" => Jpeg, _ => Webp };

        Validate(Valid() with { LogoBytes = bytes, LogoContentType = contentType }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Logo_AtExactlyFiveMegabytes_IsAccepted_OneByteMoreIsMsg19()
    {
        byte[] atLimit = new byte[UpdateOperatorProfileCommandValidator.MaxLogoBytes];
        Png.CopyTo(atLimit, 0);
        Validate(Valid() with { LogoBytes = atLimit, LogoContentType = "image/png" }).IsValid.Should().BeTrue();

        byte[] overLimit = new byte[UpdateOperatorProfileCommandValidator.MaxLogoBytes + 1];
        Png.CopyTo(overLimit, 0);
        AssertSingle(Valid() with { LogoBytes = overLimit, LogoContentType = "image/png" }, "Logo",
            OperatorProfileErrorCodes.InvalidLogo);
    }

    [Fact]
    public void Logo_EmptyBytes_IsMsg19_ThisIsHowTheControllerFlagsAnOversizedUpload()
    {
        AssertSingle(Valid() with { LogoBytes = [], LogoContentType = "image/png" }, "Logo",
            OperatorProfileErrorCodes.InvalidLogo);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/svg+xml")]
    [InlineData("application/pdf")]
    [InlineData("")]
    [InlineData(null)]
    public void Logo_RejectsUnsupportedContentTypes_Msg19(string? contentType)
    {
        AssertSingle(Valid() with { LogoBytes = Png, LogoContentType = contentType }, "Logo",
            OperatorProfileErrorCodes.InvalidLogo);
    }

    [Fact]
    public void Logo_RejectsContentTypeThatDoesNotMatchTheFileSignature_Msg19()
    {
        AssertSingle(Valid() with { LogoBytes = Jpeg, LogoContentType = "image/png" }, "Logo",
            OperatorProfileErrorCodes.InvalidLogo);
        AssertSingle(Valid() with { LogoBytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], LogoContentType = "image/webp" },
            "Logo", OperatorProfileErrorCodes.InvalidLogo);
    }

    [Fact]
    public void RemoveLogo_WithoutLogo_IsValid()
    {
        Validate(Valid() with { RemoveLogo = true }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RemoveLogo_WithNewLogo_IsValid_TheNewLogoWins()
    {
        Validate(Valid() with { RemoveLogo = true, LogoBytes = Png, LogoContentType = "image/png" })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void RemoveLogo_WithAnInvalidNewLogo_StillReportsTheLogoError()
    {
        AssertSingle(Valid() with { RemoveLogo = true, LogoBytes = [], LogoContentType = "image/png" }, "Logo",
            OperatorProfileErrorCodes.InvalidLogo);
    }

    [Fact]
    public void SeveralFailures_AreAllReported_OnePerField()
    {
        var result = Validate(Valid() with
        {
            BusinessName = "",
            ContactEmail = "bad",
            ContactPhone = "123",
            Website = "nope",
            LogoBytes = [],
            LogoContentType = "image/png",
        });

        result.Errors.Select(error => (error.PropertyName, error.ErrorCode)).Should().BeEquivalentTo(new[]
        {
            ("BusinessName", OperatorProfileErrorCodes.Required),
            ("ContactEmail", OperatorProfileErrorCodes.InvalidEmail),
            ("ContactPhone", OperatorProfileErrorCodes.InvalidPhone),
            ("Website", OperatorProfileErrorCodes.RequestInvalid),
            ("Logo", OperatorProfileErrorCodes.InvalidLogo),
        });
    }

    private ValidationResult Validate(UpdateOperatorProfileCommand command) => validator.Validate(command);

    private void AssertSingle(UpdateOperatorProfileCommand command, string property, string errorCode)
    {
        var result = Validate(command);

        result.Errors.Should().ContainSingle();
        result.Errors[0].PropertyName.Should().Be(property);
        result.Errors[0].ErrorCode.Should().Be(errorCode);
    }

    private static UpdateOperatorProfileCommand Valid() => new("Sun Tours", null, null, null, null, null, null, null, false);
}