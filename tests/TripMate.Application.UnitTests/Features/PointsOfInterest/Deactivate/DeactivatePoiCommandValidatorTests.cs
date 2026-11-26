using FluentAssertions;

using TripMate.Application.Features.PointsOfInterest.Deactivate;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Deactivate;

public class DeactivatePoiCommandValidatorTests
{
    private readonly DeactivatePoiCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_PassesValidation()
    {
        var command = new DeactivatePoiCommand
        {
            PoiId = 1,
            Reason = "Permanent closure due to road works.",
            ReferenceSnapshotToken = "ABCDEF123456"
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidPoiId_FailsWithInvalidInput(long poiId)
    {
        var command = new DeactivatePoiCommand
        {
            PoiId = poiId,
            Reason = "Valid reason.",
            ReferenceSnapshotToken = "ABCDEF123456"
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(DeactivatePoiCommand.PoiId)
            && e.ErrorCode == PoiDeactivationErrors.InvalidInput);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_EmptyOrWhitespaceReason_FailsWithMsg123(string? reason)
    {
        var command = new DeactivatePoiCommand
        {
            PoiId = 1,
            Reason = reason!,
            ReferenceSnapshotToken = "ABCDEF123456"
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(DeactivatePoiCommand.Reason)
            && e.ErrorCode == PoiDeactivationErrors.Msg123
            && e.ErrorMessage == PoiDeactivationErrors.Msg123Message);
    }

    [Fact]
    public void Validate_ReasonExceedingMaxLength_FailsWithInvalidInput()
    {
        var command = new DeactivatePoiCommand
        {
            PoiId = 1,
            Reason = new string('a', DeactivatePoiCommandValidator.ReasonMaxLength + 1),
            ReferenceSnapshotToken = "ABCDEF123456"
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(DeactivatePoiCommand.Reason)
            && e.ErrorCode == PoiDeactivationErrors.InvalidInput);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_EmptyOrWhitespaceToken_FailsWithInvalidInput(string? token)
    {
        var command = new DeactivatePoiCommand
        {
            PoiId = 1,
            Reason = "Valid reason.",
            ReferenceSnapshotToken = token!
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(DeactivatePoiCommand.ReferenceSnapshotToken)
            && e.ErrorCode == PoiDeactivationErrors.InvalidInput);
    }
}