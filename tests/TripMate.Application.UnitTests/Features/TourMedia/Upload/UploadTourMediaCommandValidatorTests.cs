using FluentAssertions;

using TripMate.Application.Features.TourMedia.Upload;

namespace TripMate.Application.UnitTests.Features.TourMedia.Upload;

public sealed class UploadTourMediaCommandValidatorTests
{
    [Fact]
    public void Validate_ValidCommand_Succeeds()
    {
        var result = new UploadTourMediaCommandValidator().Validate(CreateCommand());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveTourId_Fails(long tourId)
    {
        var command = CreateCommand() with { TourId = tourId };

        var result = new UploadTourMediaCommandValidator().Validate(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptyIdempotencyKeyAndBlankAltText_Fails()
    {
        var command = CreateCommand() with
        {
            IdempotencyKey = Guid.Empty,
            AltText = " ",
        };

        var result = new UploadTourMediaCommandValidator().Validate(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_OverlongCaptionOrAltText_Fails()
    {
        var command = CreateCommand() with
        {
            Caption = new string('a', 501),
            AltText = new string('b', 501),
        };

        var result = new UploadTourMediaCommandValidator().Validate(command);

        result.IsValid.Should().BeFalse();
    }

    private static UploadTourMediaCommand CreateCommand() => new(
        TourId: 42,
        CurrentUserId: 7,
        IdempotencyKey: Guid.NewGuid(),
        Image: new(
            new MemoryStream([1, 2, 3]),
            3,
            "tour.png",
            "image/png"),
        Caption: "A tour image",
        AltText: "Travelers at a river",
        IsPrimary: false);
}