namespace TripMate.Domain.Constants;

public static class PoiCategoryNames
{
    /// <summary>
    /// Restaurants and cafés. Never auto-scheduled by itinerary generation; travelers add them
    /// to an itinerary themselves.
    /// </summary>
    public const string FoodAndDrink = "Food & Drink";

    public static bool IsFoodAndDrink(string? categoryName) =>
        string.Equals(categoryName?.Trim(), FoodAndDrink, StringComparison.OrdinalIgnoreCase);
}