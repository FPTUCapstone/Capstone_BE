namespace TripMate.Domain.Constants;

public static class PoiCategoryNames
{
    public const string Beach = "Beach";
    public const string Culture = "Culture";
    public const string FoodAndDrink = "Food & Drink";
    public const string Museum = "Museum";
    public const string NaturalAttraction = "Natural attraction";
    public const string Nature = "Nature";
    public const string ThemePark = "Theme park";
    public const string WaterPark = "Water park";

    public static readonly IReadOnlySet<string> AllCanonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Beach,
        Culture,
        FoodAndDrink,
        Museum,
        NaturalAttraction,
        Nature,
        ThemePark,
        WaterPark,
    };

    /// <summary>
    /// Restaurants and cafés. Never auto-scheduled by itinerary generation; travelers add them
    /// to an itinerary themselves.
    /// </summary>
    public static readonly string[] FoodAndDrinkAliases =
    [
        FoodAndDrink,
        "Food and Drink",
        "Ẩm thực",
        "Ăn uống",
        "Restaurant",
        "Nhà hàng",
        "Quán ăn",
        "Café",
        "Cafe",
        "Coffee",
    ];

    public static bool IsFoodAndDrink(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return false;
        }

        var trimmed = categoryName.Trim();
        return FoodAndDrinkAliases.Any(alias =>
            string.Equals(trimmed, alias, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Ánh xạ tên danh mục về khóa chuẩn hóa trong PoiCategoryNames.AllCanonical.
    /// Nếu danh mục là null hoặc rỗng, trả về "General".
    /// </summary>
    public static string NormalizeToCanonical(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return "General";
        }

        var trimmed = categoryName.Trim();
        foreach (var canonical in AllCanonical)
        {
            if (string.Equals(trimmed, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }

        if (IsFoodAndDrink(trimmed))
        {
            return FoodAndDrink;
        }

        if (string.Equals(trimmed, "NaturalAttraction", StringComparison.OrdinalIgnoreCase))
        {
            return NaturalAttraction;
        }

        return trimmed;
    }
}