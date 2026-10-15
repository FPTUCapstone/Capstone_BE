using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.Features.Traveler.Preferences.Common;

/// <summary>Canonical option keys of UC-09 (spec §3). Keys are English and never localized.</summary>
public static class TravelerPreferenceKeys
{
    public const int MaxInterests = 10;

    public const string FoodNoRestriction = "NoRestriction";
    public const string FoodVegetarian = "Vegetarian";
    public const string FoodVegan = "Vegan";
    public const string FoodHalal = "Halal";
    public const string FoodNoSeafood = "NoSeafood";

    public static readonly string[] FoodPreferences =
    [
        FoodNoRestriction,
        FoodVegetarian,
        FoodVegan,
        FoodHalal,
        FoodNoSeafood,
    ];

    public const string InterestSourceCategory = "Category";
    public const string InterestSourceTag = "Tag";

    /// <summary>SRS BR-87 wording of the vehicle commercial-service category.</summary>
    public const string CommercialCategoryVehicleRental = "Vehicle Rental";

    /// <summary>
    /// POI category names that mark commercial services and are never offered as interests (D-07).
    /// </summary>
    public static readonly string[] ExcludedInterestCategories =
    [
        CommercialService.CategoryHotel,
        CommercialService.CategoryRestaurant,
        CommercialService.CategoryVehicle,
        CommercialCategoryVehicleRental,
    ];

    public static readonly string[] TransportModes = Enum.GetNames<TransportMode>();

    public static readonly string[] TravelPaces = Enum.GetNames<TravelerPace>();

    public static readonly string[] RiskTolerances = Enum.GetNames<RiskToleranceLevel>();

    /// <summary>Resolves an enum key by name only, case-insensitively; numeric strings are rejected.</summary>
    public static bool TryParseKey<TEnum>(string? value, out TEnum? result)
        where TEnum : struct, Enum
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var name = Enum.GetNames<TEnum>()
            .FirstOrDefault(candidate => string.Equals(
                candidate,
                value.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return false;
        }

        result = Enum.Parse<TEnum>(name);
        return true;
    }

    /// <summary>Returns the canonical food key, or null when the value is not a food key.</summary>
    public static string? CanonicalFoodKey(string? value) =>
        value is null
            ? null
            : FoodPreferences.FirstOrDefault(key => string.Equals(
                key,
                value.Trim(),
                StringComparison.OrdinalIgnoreCase));
}