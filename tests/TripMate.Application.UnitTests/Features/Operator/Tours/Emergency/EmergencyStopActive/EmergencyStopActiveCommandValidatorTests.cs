using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.Operator.Tours.Emergency.EmergencyStopActive;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Emergency.EmergencyStopActive;

public sealed class EmergencyStopActiveCommandValidatorTests
{
    [Fact]
    public async Task Validate_ShortReasonDetails_Passes()
    {
        // Canonical spec only requires reasonDetails to be present; no minimum length exists.
        var command = new EmergencyStopActiveCommand(1, 2, Guid.NewGuid(), new string('a', 64),
            "OperatorPass123!", EmergencyTourReason.SafetyIncident,
            "Landslide", "Stay with the guide.", "0900000000");

        var result = await new EmergencyStopActiveCommandValidator().ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_NonHttpsEvidence_Fails()
    {
        var command = new EmergencyStopActiveCommand(1, 2, Guid.NewGuid(), new string('a', 64),
            "OperatorPass123!", EmergencyTourReason.SafetyIncident,
            "Road access is unsafe.", "Stay with the guide.", "0900000000",
            EvidenceUrl: "http://user:password@example.test/evidence");

        var result = await new EmergencyStopActiveCommandValidator().ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }
}
