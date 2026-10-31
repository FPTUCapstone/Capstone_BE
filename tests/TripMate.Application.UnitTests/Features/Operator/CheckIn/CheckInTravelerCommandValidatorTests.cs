using FluentAssertions;

using TripMate.Application.Features.Operator.CheckIn.Scan;

namespace TripMate.Application.UnitTests.Features.Operator.CheckIn;

public sealed class CheckInTravelerCommandValidatorTests
{
    private readonly CheckInTravelerCommandValidator validator = new();

    [Fact]
    public void Validate_WhenScheduleIdIsZero_Fails()
    {
        var command = new CheckInTravelerCommand(0, QrContent: "TM1.test.sig");
        var result = validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CheckInTravelerCommand.ScheduleId));
    }

    [Fact]
    public void Validate_WhenBothQrContentAndBookingCodeAreEmpty_Fails()
    {
        var command = new CheckInTravelerCommand(1, QrContent: null, BookingCode: null);
        var result = validator.Validate(command);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenQrContentProvided_Passes()
    {
        var command = new CheckInTravelerCommand(1, QrContent: "TM1.valid.sig");
        var result = validator.Validate(command);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenBookingCodeProvided_Passes()
    {
        var command = new CheckInTravelerCommand(1, BookingCode: "BK-20261030-001");
        var result = validator.Validate(command);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenCoordinatesOutOfRange_Fails()
    {
        var command = new CheckInTravelerCommand(1, QrContent: "TM1.valid.sig", Latitude: 120m, Longitude: 200m);
        var result = validator.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CheckInTravelerCommand.Latitude));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CheckInTravelerCommand.Longitude));
    }
}