using FluentAssertions;

using TripMate.Application.Features.Scheduling.Create;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandValidatorTests
{
    private readonly CreateSchedulingRequestCommandValidator _validator = new();

    [Fact]
    public void Validate_WithApprovedRequestContract_HasNoErrors()
    {
        var result = _validator.Validate(CreateValidCommand());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEndChoiceIsAmbiguous_HasAnError()
    {
        var command = CreateValidCommand() with { EndPoiId = 12, ReturnToStart = true };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(command.EndPoiId));
    }

    [Fact]
    public void Validate_WhenMandatoryLocationsAreDuplicated_HasAnError()
    {
        var command = CreateValidCommand() with { MandatoryPoiIds = [12, 12] };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(command.MandatoryPoiIds));
    }

    private static CreateSchedulingRequestCommand CreateValidCommand() => new(
        TravelerUserId: 42,
        IdempotencyKey: Guid.NewGuid(),
        StartAt: new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(7)),
        TimeZoneId: "Asia/Ho_Chi_Minh",
        StartLatitude: 16.0544m,
        StartLongitude: 108.2022m,
        ExplorationLatitude: 16.0471m,
        ExplorationLongitude: 108.2068m,
        EndPoiId: null,
        ReturnToStart: true,
        AvailableMinutes: 480,
        TransportMode: TransportMode.Motorbike,
        SearchRadiusKm: 10m,
        BudgetVnd: 800_000m,
        MandatoryPoiIds: [12],
        RestPreference: RestPreference.Auto);
}