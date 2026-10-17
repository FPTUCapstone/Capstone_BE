using FluentValidation;

using TripMate.Application.Features.Scheduling.Common;
using TripMate.Application.Features.Traveler.Preferences.Common;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Preferences.Update;

/// <summary>
/// Catalogue-independent UC-09 rules. Interest membership in the catalogue is checked by the
/// handler because it needs the database. Every failure message is the stable error code so
/// clients can map it to their message catalog (D-04).
/// </summary>
public sealed class UpdateTravelerPreferencesCommandValidator
    : AbstractValidator<UpdateTravelerPreferencesCommand>
{
    /// <summary>Longest catalogue name (POI category, NVARCHAR(100)).</summary>
    private const int MaxInterestKeyLength = 100;

    public UpdateTravelerPreferencesCommandValidator()
    {
        RuleFor(command => command.TravelerUserId).GreaterThan(0);

        RuleFor(command => command.Interests)
            .Must(BeWellFormedInterestValues)
            .WithErrorCode(TravelerPreferenceErrors.InvalidOption)
            .WithMessage(TravelerPreferenceErrors.InvalidOption)
            .Must(HaveAtMostMaxDistinctInterests)
            .WithErrorCode(TravelerPreferenceErrors.TooManyInterests)
            .WithMessage(TravelerPreferenceErrors.TooManyInterests);

        RuleFor(command => command.PreferredTransportMode)
            .Must(value => TravelerPreferenceKeys.TryParseKey<TransportMode>(value, out _))
            .WithErrorCode(TravelerPreferenceErrors.InvalidOption)
            .WithMessage(TravelerPreferenceErrors.InvalidOption);

        RuleFor(command => command.TravelPace)
            .Must(value => TravelerPreferenceKeys.TryParseKey<TravelerPace>(value, out _))
            .WithErrorCode(TravelerPreferenceErrors.InvalidOption)
            .WithMessage(TravelerPreferenceErrors.InvalidOption);

        RuleFor(command => command.RiskTolerance)
            .Must(value => TravelerPreferenceKeys.TryParseKey<RiskToleranceLevel>(value, out _))
            .WithErrorCode(TravelerPreferenceErrors.InvalidOption)
            .WithMessage(TravelerPreferenceErrors.InvalidOption);

        RuleFor(command => command.FoodPreferences)
            .Cascade(CascadeMode.Stop)
            .Must(BeKnownFoodKeys)
            .WithErrorCode(TravelerPreferenceErrors.InvalidOption)
            .WithMessage(TravelerPreferenceErrors.InvalidOption)
            .Must(KeepNoRestrictionExclusive)
            .WithErrorCode(TravelerPreferenceErrors.FoodNoRestrictionExclusive)
            .WithMessage(TravelerPreferenceErrors.FoodNoRestrictionExclusive);

    }

    private static bool BeWellFormedInterestValues(IReadOnlyList<string?>? interests) =>
        interests is null
        || interests.All(value =>
            !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= MaxInterestKeyLength);

    private static bool HaveAtMostMaxDistinctInterests(IReadOnlyList<string?>? interests) =>
        interests is null
        || interests
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => TravelerPreferenceScoring.NormalizePreferenceToken(value!))
            .Distinct(StringComparer.Ordinal)
            .Count() <= TravelerPreferenceKeys.MaxInterests;

    private static bool BeKnownFoodKeys(IReadOnlyList<string?>? food) =>
        food is null || food.All(value => TravelerPreferenceKeys.CanonicalFoodKey(value) is not null);

    private static bool KeepNoRestrictionExclusive(IReadOnlyList<string?>? food)
    {
        if (food is null)
        {
            return true;
        }

        var keys = food
            .Select(TravelerPreferenceKeys.CanonicalFoodKey)
            .Where(key => key is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return !keys.Contains(TravelerPreferenceKeys.FoodNoRestriction) || keys.Length == 1;
    }
}