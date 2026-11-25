using FluentAssertions;

using TripMate.Application.Features.Traveler.Bookings.Emergency;
using TripMate.Application.Features.Traveler.Bookings.Emergency.Acknowledge;
using TripMate.Application.Features.Traveler.Bookings.Emergency.GetStatus;

namespace TripMate.Application.UnitTests.Features.Traveler.Bookings.Emergency;

public sealed class EmergencyStatusValidatorTests
{
    [Fact]
    public async Task Validate_NonPositiveBookingIds_Fail()
    {
        var getResult = await new GetTourBookingEmergencyStatusQueryValidator()
            .ValidateAsync(new GetTourBookingEmergencyStatusQuery(0));
        var acknowledgeResult = await new AcknowledgeTourBookingEmergencyStatusCommandValidator()
            .ValidateAsync(new AcknowledgeTourBookingEmergencyStatusCommand(0));

        getResult.IsValid.Should().BeFalse();
        acknowledgeResult.IsValid.Should().BeFalse();
    }
}