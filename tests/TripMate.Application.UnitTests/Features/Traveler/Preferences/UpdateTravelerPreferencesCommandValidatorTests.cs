using FluentAssertions;

using TripMate.Application.Features.Traveler.Preferences.Common;
using TripMate.Application.Features.Traveler.Preferences.Update;

namespace TripMate.Application.UnitTests.Features.Traveler.Preferences;

public sealed class UpdateTravelerPreferencesCommandValidatorTests
{
    private readonly UpdateTravelerPreferencesCommandValidator _validator = new();

    [Fact]
    public void Validate_AllNullOrEmpty_IsValid()
    {
        _validator.Validate(Command()).IsValid.Should().BeTrue();
        _validator.Validate(Command(interests: [], food: [])).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("motorbike", "relaxed", "low")]
    [InlineData("PublicTransit", "Fast", "High")]
    [InlineData("WALKING", "MODERATE", "MEDIUM")]
    public void Validate_EnumKeysInAnyCasing_AreValid(string transport, string pace, string risk)
    {
        var result = _validator.Validate(Command(transport: transport, pace: pace, risk: risk));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Bicycle", nameof(UpdateTravelerPreferencesCommand.PreferredTransportMode))]
    [InlineData("1", nameof(UpdateTravelerPreferencesCommand.PreferredTransportMode))]
    public void Validate_UnknownTransport_ReportsInvalidOption(string value, string property)
    {
        AssertSingleError(Command(transport: value), property, TravelerPreferenceErrors.InvalidOption);
    }

    [Fact]
    public void Validate_UnknownPaceAndRisk_ReportInvalidOption()
    {
        AssertSingleError(
            Command(pace: "Packed"),
            nameof(UpdateTravelerPreferencesCommand.TravelPace),
            TravelerPreferenceErrors.InvalidOption);
        AssertSingleError(
            Command(risk: "Extreme"),
            nameof(UpdateTravelerPreferencesCommand.RiskTolerance),
            TravelerPreferenceErrors.InvalidOption);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankInterest_ReportsInvalidOption(string blank)
    {
        AssertSingleError(
            Command(interests: ["Beach", blank]),
            nameof(UpdateTravelerPreferencesCommand.Interests),
            TravelerPreferenceErrors.InvalidOption);
    }

    [Fact]
    public void Validate_TenDistinctInterests_IsValid()
    {
        var interests = Enumerable.Range(1, 10).Select(index => $"Interest {index}").ToArray();

        _validator.Validate(Command(interests: interests)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ElevenDistinctInterests_ReportsTooManyInterests()
    {
        var interests = Enumerable.Range(1, 11).Select(index => $"Interest {index}").ToArray();

        AssertSingleError(
            Command(interests: interests),
            nameof(UpdateTravelerPreferencesCommand.Interests),
            TravelerPreferenceErrors.TooManyInterests);
    }

    [Fact]
    public void Validate_DuplicatesDifferingByCaseOrDiacritics_CountOnce()
    {
        var interests = Enumerable.Range(1, 10).Select(index => $"Interest {index}")
            .Append("INTEREST 1")
            .Append("Ìnterest 2")
            .ToArray();

        _validator.Validate(Command(interests: interests)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_UnknownFoodKey_ReportsInvalidOption()
    {
        AssertSingleError(
            Command(food: ["Vegan", "Keto"]),
            nameof(UpdateTravelerPreferencesCommand.FoodPreferences),
            TravelerPreferenceErrors.InvalidOption);
    }

    [Fact]
    public void Validate_NoRestrictionCombinedWithAnotherKey_ReportsExclusiveError()
    {
        AssertSingleError(
            Command(food: ["NoRestriction", "vegan"]),
            nameof(UpdateTravelerPreferencesCommand.FoodPreferences),
            TravelerPreferenceErrors.FoodNoRestrictionExclusive);
    }

    [Fact]
    public void Validate_NoRestrictionAloneOrRepeated_IsValid()
    {
        _validator.Validate(Command(food: ["NoRestriction"])).IsValid.Should().BeTrue();
        _validator.Validate(Command(food: ["NoRestriction", "norestriction"])).IsValid.Should().BeTrue();
        _validator.Validate(Command(food: ["Vegetarian", "Halal", "NoSeafood"])).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ErrorMessagesAreTheStableCodes()
    {
        var result = _validator.Validate(Command(transport: "Boat"));

        result.Errors.Should().OnlyContain(error => error.ErrorMessage == error.ErrorCode);
    }

    private void AssertSingleError(
        UpdateTravelerPreferencesCommand command,
        string property,
        string errorCode)
    {
        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.Errors[0].PropertyName.Should().Be(property);
        result.Errors[0].ErrorCode.Should().Be(errorCode);
        result.Errors[0].ErrorMessage.Should().Be(errorCode);
    }

    private static UpdateTravelerPreferencesCommand Command(
        string?[]? interests = null,
        string? transport = null,
        string? pace = null,
        string? risk = null,
        string?[]? food = null) =>
        new(7, interests, transport, pace, risk, food);
}