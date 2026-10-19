using FluentAssertions;
using Microsoft.Extensions.Options;
using TripMate.Application.Features.Scheduling.Personalization;
using TripMate.Domain.Constants;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using Xunit;

namespace TripMate.Application.UnitTests.Features.Scheduling.Personalization;

public class PersonalParamsFactoryTests
{
    private static PersonalizationOptions CreateStandardOptions() => new()
    {
        Version = "2026.1",
        Pace = new Dictionary<TravelerPace, PaceParams>
        {
            [TravelerPace.Relaxed] = new(LambdaPerMin: 0.025, MaxStopsPerDay: 3, MaxDailyEffort: 200, RestAfterMin: 90),
            [TravelerPace.Moderate] = new(LambdaPerMin: 0.015, MaxStopsPerDay: 5, MaxDailyEffort: 300, RestAfterMin: 120),
            [TravelerPace.Fast] = new(LambdaPerMin: 0.008, MaxStopsPerDay: 8, MaxDailyEffort: 450, RestAfterMin: 180),
        },
        Mu = 0.005,
        GammaDefault = 0.6,
        GammaLoved = 0.85,
        RhoOutdoor = 0.25,
        RhoRest = 0.20,
        RhoStops = 0.30,
        RhoEffortPerHour = 0.05,
        TagToCategory = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["beach"] = [PoiCategoryNames.Beach],
            ["photography"] = [PoiCategoryNames.Culture],
            ["heritage"] = [PoiCategoryNames.Culture],
            ["museum"] = [PoiCategoryNames.Museum],
            ["nature"] = [PoiCategoryNames.Nature, PoiCategoryNames.NaturalAttraction],
            ["outdoor"] = [PoiCategoryNames.Nature, PoiCategoryNames.NaturalAttraction, PoiCategoryNames.Beach],
            ["food"] = [PoiCategoryNames.FoodAndDrink],
        },
    };

    [Fact]
    public void Persona1_Active_HighRisk_Generates_FastPace_With_LovedCategories()
    {
        var options = CreateStandardOptions();
        var factory = new PersonalParamsFactory(Microsoft.Extensions.Options.Options.Create(options));

        var profile = TravelerProfile.CreateEmpty(1, DateTimeOffset.UtcNow);
        profile.ReplacePreferences(
            interestTagsJson: "[\"beach\", \"photography\"]",
            preferredTransportMode: null,
            travelPace: TravelerPace.Fast,
            riskTolerance: RiskToleranceLevel.High,
            foodPreferencesJson: null,
            nowUtc: DateTimeOffset.UtcNow);

        var p = factory.Create(profile);

        p.LambdaPerMin.Should().Be(0.008, "Fast pace has smallest lambda");
        p.MaxStopsPerDay.Should().Be(8, "Fast pace has largest max stops");
        p.MaxDailyEffort.Should().Be((int)Math.Round(450 * 1.2), "High risk scales effort by 1.2");
        p.MaxConsecutiveOutdoor.Should().Be(3);
        p.LovedCategories.Should().Contain(PoiCategoryNames.Beach);
        p.LovedCategories.Should().Contain(PoiCategoryNames.Culture);
        p.ConfigVersion.Should().Be("2026.1");
    }

    [Fact]
    public void Persona2_Culture_Moderate_Generates_DefaultModerate_With_CultureCategories()
    {
        var options = CreateStandardOptions();
        var factory = new PersonalParamsFactory(Microsoft.Extensions.Options.Options.Create(options));

        var profile = TravelerProfile.CreateEmpty(2, DateTimeOffset.UtcNow);
        profile.ReplacePreferences(
            interestTagsJson: "[\"heritage\", \"museum\"]",
            preferredTransportMode: null,
            travelPace: TravelerPace.Moderate,
            riskTolerance: RiskToleranceLevel.Medium,
            foodPreferencesJson: null,
            nowUtc: DateTimeOffset.UtcNow);

        var p = factory.Create(profile);

        p.LambdaPerMin.Should().Be(0.015);
        p.MaxStopsPerDay.Should().Be(5);
        p.MaxDailyEffort.Should().Be(300);
        p.MaxConsecutiveOutdoor.Should().Be(3);
        p.LovedCategories.Should().BeEquivalentTo([PoiCategoryNames.Culture, PoiCategoryNames.Museum]);
        p.ConfigVersion.Should().Be("2026.1");
    }

    [Fact]
    public void Persona3_Caution_Relaxed_LowRisk_Generates_ReducedEffort_And_FewerOutdoors()
    {
        var options = CreateStandardOptions();
        var factory = new PersonalParamsFactory(Microsoft.Extensions.Options.Options.Create(options));

        var profile = TravelerProfile.CreateEmpty(3, DateTimeOffset.UtcNow);
        profile.ReplacePreferences(
            interestTagsJson: null,
            preferredTransportMode: null,
            travelPace: TravelerPace.Relaxed,
            riskTolerance: RiskToleranceLevel.Low,
            foodPreferencesJson: null,
            nowUtc: DateTimeOffset.UtcNow);

        var p = factory.Create(profile);

        p.LambdaPerMin.Should().Be(0.025, "Relaxed pace has largest lambda");
        p.MaxStopsPerDay.Should().Be(3, "Relaxed pace has lowest max stops");
        p.MaxDailyEffort.Should().Be((int)Math.Round(200 * 0.7), "Low risk scales effort by 0.7");
        p.MaxConsecutiveOutdoor.Should().Be(2, "Low risk reduces max consecutive outdoor stops to 2");
        p.LovedCategories.Should().BeEmpty();
    }

    [Fact]
    public void Persona4_NullProfile_FallsBack_To_Moderate_Defaults()
    {
        var options = CreateStandardOptions();
        var factory = new PersonalParamsFactory(Microsoft.Extensions.Options.Options.Create(options));

        var p = factory.Create(null);

        p.LambdaPerMin.Should().Be(0.015);
        p.MaxStopsPerDay.Should().Be(5);
        p.MaxDailyEffort.Should().Be(300);
        p.RestAfterMin.Should().Be(120);
        p.MaxConsecutiveOutdoor.Should().Be(3);
        p.SunriseFactor.Should().Be(0.6);
        p.LovedCategories.Should().BeEmpty();
        p.ConfigVersion.Should().Be("2026.1");
    }

    [Fact]
    public void Persona5_CorruptJson_DoesNotThrow_And_Returns_EmptyLovedCategories()
    {
        var options = CreateStandardOptions();
        var factory = new PersonalParamsFactory(Microsoft.Extensions.Options.Options.Create(options));

        var profile = TravelerProfile.CreateEmpty(5, DateTimeOffset.UtcNow);
        profile.ReplacePreferences(
            interestTagsJson: "{broken json: true",
            preferredTransportMode: null,
            travelPace: TravelerPace.Moderate,
            riskTolerance: null,
            foodPreferencesJson: null,
            nowUtc: DateTimeOffset.UtcNow);

        var action = () => factory.Create(profile);
        action.Should().NotThrow();

        var p = action();
        p.LovedCategories.Should().BeEmpty();
        p.MaxStopsPerDay.Should().Be(5);
    }

    [Fact]
    public void OptionsValidator_Fails_When_Pace_Level_Is_Missing()
    {
        var validator = new PersonalizationOptionsValidator();
        var options = CreateStandardOptions();
        options.Pace.Remove(TravelerPace.Fast);

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("missing configuration for 'Fast'");
    }

    [Fact]
    public void OptionsValidator_Fails_When_Monotonicity_Is_Violated()
    {
        var validator = new PersonalizationOptionsValidator();

        // 1. Vi phạm lambda monotonicity: Fast có lambda lớn hơn Moderate
        var invalidLambdaOptions = CreateStandardOptions();
        invalidLambdaOptions.Pace[TravelerPace.Fast] = invalidLambdaOptions.Pace[TravelerPace.Fast] with { LambdaPerMin = 0.05 };
        var lambdaResult = validator.Validate(null, invalidLambdaOptions);
        lambdaResult.Failed.Should().BeTrue();
        lambdaResult.FailureMessage.Should().Contain("LambdaPerMin must satisfy monotonic order");

        // 2. Vi phạm MaxStops monotonicity: Fast có MaxStops nhỏ hơn Relaxed
        var invalidStopsOptions = CreateStandardOptions();
        invalidStopsOptions.Pace[TravelerPace.Fast] = invalidStopsOptions.Pace[TravelerPace.Fast] with { MaxStopsPerDay = 2 };
        var stopsResult = validator.Validate(null, invalidStopsOptions);
        stopsResult.Failed.Should().BeTrue();
        stopsResult.FailureMessage.Should().Contain("MaxStopsPerDay must satisfy monotonic order");
    }

    [Fact]
    public void All_TagToCategory_Values_Must_Exist_In_Canonical_POICategories()
    {
        var standardOptions = CreateStandardOptions();

        foreach (var categories in standardOptions.TagToCategory.Values)
        {
            foreach (var category in categories)
            {
                PoiCategoryNames.AllCanonical.Should().Contain(
                    category,
                    $"Standard TagToCategory value '{category}' must be one of the 8 canonical database POI categories");
            }
        }
    }

    [Fact]
    public void Persona_With_Nature_Tag_Contains_Both_Nature_And_NaturalAttraction_In_LovedCategories()
    {
        var options = CreateStandardOptions();
        var factory = new PersonalParamsFactory(Microsoft.Extensions.Options.Options.Create(options));

        var profile = TravelerProfile.CreateEmpty(1, DateTimeOffset.UtcNow);
        profile.ReplacePreferences(
            interestTagsJson: "[\"nature\"]",
            preferredTransportMode: null,
            travelPace: TravelerPace.Moderate,
            riskTolerance: RiskToleranceLevel.Medium,
            foodPreferencesJson: null,
            nowUtc: DateTimeOffset.UtcNow);

        var p = factory.Create(profile);

        p.LovedCategories.Should().Contain(PoiCategoryNames.Nature);
        p.LovedCategories.Should().Contain(PoiCategoryNames.NaturalAttraction);
    }

    [Fact]
    public void Empty_PersonalizationOptions_Fails_Validation_Requiring_Configuration()
    {
        var validator = new PersonalizationOptionsValidator();
        var emptyOptions = new PersonalizationOptions();

        var result = validator.Validate(null, emptyOptions);

        result.Failed.Should().BeTrue("Stripped PersonalizationOptions without Pace configuration must fail validation");
        result.FailureMessage.Should().Contain("Pace");
    }


    [Fact]
    public void Validation_Fails_When_RhoEffortPerHour_Is_Negative()
    {
        var validator = new PersonalizationOptionsValidator();
        var standard = CreateStandardOptions();
        var options = new PersonalizationOptions
        {
            Version = standard.Version,
            Pace = standard.Pace,
            RhoEffortPerHour = -0.01,
        };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("RhoEffortPerHour");
    }
}
