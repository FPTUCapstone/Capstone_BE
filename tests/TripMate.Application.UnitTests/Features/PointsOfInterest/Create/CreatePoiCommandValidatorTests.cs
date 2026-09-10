using FluentAssertions;

using TripMate.Application.Features.PointsOfInterest.Create;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Create;

public class CreatePoiCommandValidatorTests
{
    private readonly CreatePoiCommandValidator _validator = new();

    [Fact]
    public void Validate_WithValidMinimalCommand_HasNoErrors()
    {
        var result = _validator.Validate(ValidCommand());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingName_HasErrors(string name)
    {
        var result = _validator.Validate(ValidCommand() with { Name = name });

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithFieldsLongerThanSqlLimits_HasErrors()
    {
        var command = ValidCommand() with
        {
            Name = new string('n', PointOfInterest.NameMaxLength + 1),
            Address = new string('a', PointOfInterest.AddressMaxLength + 1),
            Description = new string('d', PointOfInterest.DescriptionMaxLength + 1),
        };

        var result = _validator.Validate(command);

        result.Errors.Select(error => error.PropertyName)
            .Should().Contain([
                nameof(CreatePoiCommand.Name),
                nameof(CreatePoiCommand.Address),
                nameof(CreatePoiCommand.Description),
            ]);
    }

    [Fact]
    public void Validate_WithPaddedFieldsAtSqlLimitsAfterTrimming_HasNoErrors()
    {
        var command = ValidCommand() with
        {
            Name = $"  {new string('n', PointOfInterest.NameMaxLength)}  ",
            Address = $"  {new string('a', PointOfInterest.AddressMaxLength)}  ",
            Description = $"  {new string('d', PointOfInterest.DescriptionMaxLength)}  ",
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithLongWhitespaceOnlyOptionalFields_HasNoErrors()
    {
        var command = ValidCommand() with
        {
            Address = new string(' ', PointOfInterest.AddressMaxLength + 1),
            Description = new string(' ', PointOfInterest.DescriptionMaxLength + 1),
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 10, 106)]
    [InlineData(1, -90.000001, 106)]
    [InlineData(1, 90.000001, 106)]
    [InlineData(1, 10, -180.000001)]
    [InlineData(1, 10, 180.000001)]
    public void Validate_WithInvalidRequiredReferenceOrCoordinates_HasErrors(
        int categoryId,
        decimal latitude,
        decimal longitude)
    {
        var command = ValidCommand() with
        {
            CategoryId = categoryId,
            Latitude = latitude,
            Longitude = longitude,
        };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithMissingCoordinates_HasErrors()
    {
        var command = ValidCommand() with { Latitude = null, Longitude = null };

        var result = _validator.Validate(command);

        result.Errors.Select(error => error.PropertyName)
            .Should().Contain([
                nameof(CreatePoiCommand.Latitude),
                nameof(CreatePoiCommand.Longitude),
            ]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveDuration_HasErrors(int duration)
    {
        var command = ValidCommand() with { AverageVisitDurationMinutes = duration };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 1, 1 })]
    public void Validate_WithInvalidTagIds_HasErrors(int[] tagIds)
    {
        var command = ValidCommand() with { TagIds = tagIds };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithMoreThanOneOpeningHoursEntryForADay_HasErrors()
    {
        var command = ValidCommand() with
        {
            OpeningHours =
            [
                OpenDay(1, new TimeOnly(8, 0), new TimeOnly(17, 0)),
                OpenDay(1, new TimeOnly(9, 0), new TimeOnly(18, 0)),
            ],
        };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void Validate_WithOpeningHoursDayOutsideSundayZeroRange_HasErrors(int dayOfWeek)
    {
        var command = ValidCommand() with
        {
            OpeningHours = [OpenDay(dayOfWeek, new TimeOnly(8, 0), new TimeOnly(17, 0))],
        };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithTimesOnClosedDay_HasErrors()
    {
        var command = ValidCommand() with
        {
            OpeningHours =
            [
                new CreatePoiOpeningHourInput(
                    0,
                    new TimeOnly(8, 0),
                    new TimeOnly(17, 0),
                    IsClosed: true),
            ],
        };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(InvalidOpenDays))]
    public void Validate_WithInvalidOpenDayTimes_HasErrors(CreatePoiOpeningHourInput openingHours)
    {
        var command = ValidCommand() with { OpeningHours = [openingHours] };

        _validator.Validate(command).IsValid.Should().BeFalse();
    }

    public static TheoryData<CreatePoiOpeningHourInput> InvalidOpenDays =>
        new()
        {
            OpenDay(1, null, new TimeOnly(17, 0)),
            OpenDay(1, new TimeOnly(8, 0), null),
            OpenDay(1, new TimeOnly(17, 0), new TimeOnly(8, 0)),
            OpenDay(1, new TimeOnly(8, 0), new TimeOnly(8, 0)),
        };

    private static CreatePoiCommand ValidCommand() =>
        new(
            Name: "Da Lat Flower Park",
            CategoryId: 1,
            Latitude: 11.941755m,
            Longitude: 108.438278m,
            Address: null,
            Description: null,
            IndoorOutdoor: IndoorOutdoorType.Outdoor,
            AverageVisitDurationMinutes: PointOfInterest.DefaultAverageVisitDurationMinutes,
            HasShelter: false,
            OpeningHours: null,
            TagIds: null,
            ConfirmDuplicate: false);

    private static CreatePoiOpeningHourInput OpenDay(
        int dayOfWeek,
        TimeOnly? openTime,
        TimeOnly? closeTime) =>
        new(dayOfWeek, openTime, closeTime, IsClosed: false);
}