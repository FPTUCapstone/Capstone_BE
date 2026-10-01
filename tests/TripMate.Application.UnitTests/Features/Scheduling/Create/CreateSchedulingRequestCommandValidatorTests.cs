using FluentAssertions;

using TripMate.Application.Features.Scheduling.Create;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Create;

public sealed class CreateSchedulingRequestCommandValidatorTests
{
    private static readonly DateTimeOffset FixedNowUtc = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeDateTimeProvider _clock = new() { UtcNow = FixedNowUtc };
    private readonly CreateSchedulingRequestCommandValidator _validator;

    public CreateSchedulingRequestCommandValidatorTests()
    {
        _validator = new CreateSchedulingRequestCommandValidator(_clock);
    }

    [Fact]
    public void Validate_WhenStartAtIsInPast_HasError()
    {
        // CASE BE-1: StartAt before nowUtc is rejected (06:30 +07:00 is 23:30 previous day UTC)
        var command = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(2026, 10, 20, 6, 30, 0, TimeSpan.FromHours(7)),
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(command.StartAt) &&
            error.ErrorMessage == "Start time must be in the future.");
    }

    [Fact]
    public void Validate_WhenStartAtIsEqualToCurrentInstant_HasError()
    {
        // CASE BE-2: StartAt equal to current instant (07:00 +07:00 is 00:00:00 UTC == FixedNowUtc) is rejected
        var command = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.FromHours(7)),
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(command.StartAt) &&
            error.ErrorMessage == "Start time must be in the future.");
    }

    [Fact]
    public void Validate_WhenStartAtIsInFuture_HasNoErrors()
    {
        // CASE BE-3: StartAt strictly after current instant succeeds (08:00 +07:00 is 01:00 UTC > FixedNowUtc)
        var command = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.FromHours(7)),
        };

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenStartAtWithVietnamOffsetIsPastInUtc_HasError()
    {
        // CASE BE-4: Clock is at 01:00 UTC (08:00 +07:00). 07:30 +07:00 is 00:30 UTC, which is in the past.
        var clock = new FakeDateTimeProvider
        {
            UtcNow = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
        };
        var validator = new CreateSchedulingRequestCommandValidator(clock);
        var command = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(2026, 10, 20, 7, 30, 0, TimeSpan.FromHours(7)),
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(command.StartAt) &&
            error.ErrorMessage == "Start time must be in the future.");
    }

    [Fact]
    public void Validate_WhenStartAtWithVietnamOffsetIsFutureInUtc_HasNoErrors()
    {
        // CASE BE-4: Clock is at 01:00 UTC (08:00 +07:00). 08:30 +07:00 is 01:30 UTC, which is in the future.
        var clock = new FakeDateTimeProvider
        {
            UtcNow = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero),
        };
        var validator = new CreateSchedulingRequestCommandValidator(clock);
        var command = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.FromHours(7)),
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_AcrossUtcMidnightBoundary_CorrectlyEvaluatesInstant()
    {
        // CASE BE-5: Clock is at 2026-10-19 23:50:00 UTC.
        // StartAt at 2026-10-20 07:00:00 +07:00 is 2026-10-20 00:00:00 UTC (10 minutes in the future).
        var clock = new FakeDateTimeProvider
        {
            UtcNow = new DateTimeOffset(2026, 10, 19, 23, 50, 0, TimeSpan.Zero),
        };
        var validator = new CreateSchedulingRequestCommandValidator(clock);
        var command = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.FromHours(7)),
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithDefaultParameterlessConstructor_UsesCurrentInstant()
    {
        // CASE BE-6: Parameterless constructor defaults to real-time clock
        var defaultValidator = new CreateSchedulingRequestCommandValidator();
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        var nowInVietnam = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
        var pastDay = nowInVietnam.Date.AddDays(-2);
        var futureDay = nowInVietnam.Date.AddDays(2);

        var pastCommand = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(pastDay.Year, pastDay.Month, pastDay.Day, 8, 0, 0, TimeSpan.FromHours(7)),
        };
        var futureCommand = CreateValidCommand() with
        {
            StartAt = new DateTimeOffset(futureDay.Year, futureDay.Month, futureDay.Day, 8, 0, 0, TimeSpan.FromHours(7)),
        };

        var pastResult = defaultValidator.Validate(pastCommand);
        var futureResult = defaultValidator.Validate(futureCommand);

        pastResult.IsValid.Should().BeFalse();
        pastResult.Errors.Should().Contain(e => e.PropertyName == nameof(pastCommand.StartAt));
        futureResult.IsValid.Should().BeTrue();
    }

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

    [Fact]
    public void Validate_WhenMandatoryLocationsAreOmitted_HasNoErrors()
    {
        var command = CreateValidCommand() with { MandatoryPoiIds = null };

        _validator.Validate(command).IsValid.Should().BeTrue();
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