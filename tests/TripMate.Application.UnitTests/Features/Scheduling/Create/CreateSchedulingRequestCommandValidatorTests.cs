using FluentAssertions;

using TripMate.Application.Features.Scheduling.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private readonly CreateSchedulingRequestCommandValidator _validator = new(
        new FakeDateTimeProvider { UtcNow = Now });

    [Fact]
    public void Validate_WithApprovedRequestContract_HasNoErrors()
    {
        var result = _validator.Validate(CreateValidCommand());

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(60)]
    [InlineData(135)]
    [InlineData(4320)]
    public void Validate_WhenAvailableMinutesWithinOneHourToThreeDays_HasNoErrors(int availableMinutes)
    {
        var command = CreateValidCommand() with { AvailableMinutes = availableMinutes };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(59)]
    [InlineData(4321)]
    public void Validate_WhenAvailableMinutesOutsideOneHourToThreeDays_HasAnError(int availableMinutes)
    {
        var command = CreateValidCommand() with { AvailableMinutes = availableMinutes };

        var result = _validator.Validate(command);

        result.Errors.Should().Contain(error => error.PropertyName == nameof(command.AvailableMinutes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(51)]
    [InlineData(100)]
    public void Validate_WhenSearchRadiusWithinOneToHundredKm_HasNoErrors(int searchRadiusKm)
    {
        var command = CreateValidCommand() with { SearchRadiusKm = searchRadiusKm };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100.01)]
    [InlineData(101)]
    public void Validate_WhenSearchRadiusOutsideOneToHundredKm_HasAnError(double searchRadiusKm)
    {
        var command = CreateValidCommand() with { SearchRadiusKm = (decimal)searchRadiusKm };

        var result = _validator.Validate(command);

        result.Errors.Should().Contain(error => error.PropertyName == nameof(command.SearchRadiusKm));
    }

    [Fact]
    public void Validate_WhenTripCrossesMidnight_HasNoErrors()
    {
        var command = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(2026, 10, 20, 20, 0, 0, TimeSpan.FromHours(7)),
            AvailableMinutes = 10 * 60,
        };

        _validator.Validate(command).IsValid.Should().BeTrue();
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

    [Fact]
    public void Validate_WhenMandatoryLocationsAreOmitted_HasNoErrors()
    {
        var command = CreateValidCommand() with { MandatoryPoiIds = null };

        _validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenStartAtIsInPast_HasAnError()
    {
        var command = CreateValidCommand() with { StartAt = Now.AddTicks(-1) };

        var result = _validator.Validate(command);

        result.Errors.Should().Contain(error => error.PropertyName == nameof(command.StartAt));
    }

    [Fact]
    public void Validate_WhenStartAtEqualsCurrentInstant_HasNoPastStartError()
    {
        var command = CreateValidCommand() with { StartAt = Now };

        var result = _validator.Validate(command);

        result.Errors.Should().NotContain(error => error.PropertyName == nameof(command.StartAt));
    }

    [Fact]
    public void Validate_WhenStartAtIsFutureInstantInRequestTimezone_HasNoPastStartError()
    {
        var command = CreateValidCommand() with { StartAt = Now.AddTicks(1) };

        var result = _validator.Validate(command);

        result.Errors.Should().NotContain(error => error.PropertyName == nameof(command.StartAt));
    }

    [Fact]
    public void Validate_WithDstCapableTimezone_ComparesInstantsRatherThanServerLocalTime()
    {
        var command = CreateValidCommand() with
        {
            TimeZoneId = "America/New_York",
            StartAt = new DateTimeOffset(2026, 10, 19, 20, 0, 0, TimeSpan.FromHours(-4)),
        };

        var result = _validator.Validate(command);

        result.Errors.Should().NotContain(error => error.PropertyName == nameof(command.StartAt));
    }

    [Fact]
    public void Validate_WhenTimezoneIsUnknown_PreservesTimezoneValidationError()
    {
        var command = CreateValidCommand() with { TimeZoneId = "Mars/Olympus_Mons" };

        var result = _validator.Validate(command);

        result.Errors.Should().Contain(error => error.PropertyName == nameof(command.TimeZoneId));
        result.Errors.Should().NotContain(error =>
            error.PropertyName == nameof(command.StartAt)
            && error.ErrorMessage == "Start time must not be in the past.");
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