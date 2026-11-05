using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.Operator.Tours.Emergency.CancelBeforeDeparture;
using TripMate.Application.Features.Operator.Tours.Emergency.EmergencyStopActive;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Emergency;

public sealed class EmergencyContactLimitTests
{
    private static readonly string ImpactVersion = new('a', 64);

    private static CancelBeforeDepartureCommand Cancel(string contact) => new(
        1, 1, Guid.NewGuid(), ImpactVersion, EmergencyTourReason.WeatherEmergency, "Bão đổ bộ", contact);

    private static EmergencyStopActiveCommand Stop(string contact) => new(
        1, 1, Guid.NewGuid(), ImpactVersion, "password", EmergencyTourReason.WeatherEmergency,
        "Bão đổ bộ", "Tập trung tại sảnh", contact);

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void CancelContact_IsLimitedTo100Characters(int length, bool valid)
    {
        var result = new CancelBeforeDepartureCommandValidator().Validate(Cancel(new string('1', length)));

        result.IsValid.Should().Be(valid);
        if (!valid) result.Errors[0].ErrorMessage.Should().Be("Đầu mối liên hệ tối đa 100 ký tự.");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void StopContact_IsLimitedTo100Characters(int length, bool valid)
    {
        var result = new EmergencyStopActiveCommandValidator().Validate(Stop(new string('1', length)));

        result.IsValid.Should().Be(valid);
        if (!valid) result.Errors[0].ErrorMessage.Should().Be("Đầu mối liên hệ tối đa 100 ký tự.");
    }
}
