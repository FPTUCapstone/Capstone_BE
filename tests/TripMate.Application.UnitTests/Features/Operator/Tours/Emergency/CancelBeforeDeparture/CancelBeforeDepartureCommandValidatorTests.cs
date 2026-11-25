using FluentAssertions;

using TripMate.Application.Features.Operator.Tours.Emergency;
using TripMate.Application.Features.Operator.Tours.Emergency.CancelBeforeDeparture;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Operator.Tours.Emergency.CancelBeforeDeparture;

public sealed class CancelBeforeDepartureCommandValidatorTests
{
    [Fact]
    public async Task Validate_AuthorityDirectiveWithoutEvidenceOrDeferral_Fails()
    {
        var command = new CancelBeforeDepartureCommand(1, 2, Guid.NewGuid(), new string('a', 64),
            EmergencyTourReason.AuthorityDirective, "Authority ordered cancellation.", "0900000000");

        var result = await new CancelBeforeDepartureCommandValidator().ValidateAsync(command);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_ShortReasonDetails_Passes()
    {
        // Canonical spec only requires reasonDetails to be present; no minimum length exists.
        var command = new CancelBeforeDepartureCommand(1, 2, Guid.NewGuid(), new string('a', 64),
            EmergencyTourReason.TransportFailure, "Storm", "0900000000");

        var result = await new CancelBeforeDepartureCommandValidator().ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WithoutEmergencyContact_Passes()
    {
        // Canonical spec: emergencyContact is mandatory for an active stop only.
        var command = new CancelBeforeDepartureCommand(1, 2, Guid.NewGuid(), new string('a', 64),
            EmergencyTourReason.WeatherEmergency, "Typhoon makes travel unsafe.", null!,
            EvidenceUrl: "https://authority.example.test/warnings/123");

        var result = await new CancelBeforeDepartureCommandValidator().ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_HttpsEvidenceWithoutCredentials_Passes()
    {
        var command = new CancelBeforeDepartureCommand(1, 2, Guid.NewGuid(), new string('a', 64),
            EmergencyTourReason.WeatherEmergency, "Typhoon makes travel unsafe.", "0900000000",
            EvidenceUrl: "https://authority.example.test/warnings/123");

        var result = await new CancelBeforeDepartureCommandValidator().ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}