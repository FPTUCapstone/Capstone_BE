namespace TripMate.Application.Features.Traveler.Preferences.Common;

/// <summary>
/// Stable error codes of UC-09 (spec §7.2). Clients map each code to text in their message
/// catalog (D-04); the backend returns no message text for them.
/// </summary>
public static class TravelerPreferenceErrors
{
    public const string InvalidOption = "TRAVEL_PREFERENCES.INVALID_OPTION";
    public const string TooManyInterests = "TRAVEL_PREFERENCES.TOO_MANY_INTERESTS";
    public const string FoodNoRestrictionExclusive = "TRAVEL_PREFERENCES.FOOD_NO_RESTRICTION_EXCLUSIVE";
    public const string ValueTooLong = "TRAVEL_PREFERENCES.VALUE_TOO_LONG";
    public const string Forbidden = "TRAVEL_PREFERENCES.FORBIDDEN";

    /// <summary>Error metadata key naming the offending request field.</summary>
    public const string FieldMetadataKey = "field";

    /// <summary>Error metadata key listing the rejected values.</summary>
    public const string InvalidValuesMetadataKey = "invalidValues";

    public const string InterestsField = "interests";
    public const string FoodPreferencesField = "foodPreferences";
}