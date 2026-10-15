using FluentAssertions;

using TripMate.Application.Features.Traveler.Profile.Common;
using TripMate.Application.Features.Traveler.Profile.Update;
using TripMate.Application.UnitTests.TestUtilities;

namespace TripMate.Application.UnitTests.Features.Traveler.Profile;

public sealed class UpdateTravelerProfileCommandValidatorTests
{
    private readonly UpdateTravelerProfileCommandValidator validator = new(new FakeDateTimeProvider
    {
        UtcNow = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero),
    });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyFullName_ReturnsMsg01(string fullName)
    {
        var result = validator.Validate(new UpdateTravelerProfileCommand(fullName, null, null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorCode == TravelerProfileErrorCodes.Required);
    }

    [Fact]
    public void Validate_InvalidPhone_ReturnsMsg04()
    {
        var result = validator.Validate(new UpdateTravelerProfileCommand("Traveler", "123", null));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorCode == TravelerProfileErrorCodes.InvalidPhone);
    }

    [Fact]
    public void Validate_UnderSixteenDateOfBirth_ReturnsMsg02()
    {
        var result = validator.Validate(new UpdateTravelerProfileCommand(
            "Traveler", null, new DateOnly(2010, 10, 13)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorCode == TravelerProfileErrorCodes.Invalid);
    }

    [Fact]
    public void Validate_OptionalPhoneAndBoundaryDateOfBirth_IsValid()
    {
        var result = validator.Validate(new UpdateTravelerProfileCommand(
            "Traveler", " ", new DateOnly(2010, 10, 12)));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_AvatarLargerThanFiveMegabytes_ReturnsMsg19()
    {
        var bytes = new byte[UpdateTravelerProfileCommandValidator.MaxAvatarBytes + 1];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes, 0);

        var result = validator.Validate(new UpdateTravelerProfileCommand(
            "Traveler", null, null, bytes, "image/png", false));

        result.Errors.Should().Contain(error => error.ErrorCode == TravelerProfileErrorCodes.InvalidAvatar);
    }

    [Theory]
    [InlineData("image/png", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData("application/pdf", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })]
    public void Validate_UnsupportedOrMismatchedAvatar_ReturnsMsg19(string mime, byte[] bytes)
    {
        var result = validator.Validate(new UpdateTravelerProfileCommand(
            "Traveler", null, null, bytes, mime, false));

        result.Errors.Should().Contain(error => error.ErrorCode == TravelerProfileErrorCodes.InvalidAvatar);
    }

    [Fact]
    public void Validate_AvatarCannotBeUploadedAndRemovedTogether()
    {
        var result = validator.Validate(new UpdateTravelerProfileCommand(
            "Traveler", null, null, [137, 80, 78, 71, 13, 10, 26, 10], "image/png", true));

        result.Errors.Should().Contain(error => error.ErrorCode == TravelerProfileErrorCodes.Invalid);
    }
}