namespace TripMate.Domain.Constants;

public static class PoiCategoryNames
{
    public const string Beach = "Beach";
    public const string Culture = "Culture";
    public const string FoodAndDrink = "Food & Drink";
    public const string General = "General";
    public const string Museum = "Museum";
    public const string NaturalAttraction = "Natural attraction";
    public const string Nature = "Nature";
    public const string ThemePark = "Theme park";
    public const string WaterPark = "Water park";

    public const string CulturalGroup = "Culture & Museum";
    public const string NatureGroup = "Nature & Natural attraction";

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

    /// <summary>Recognizes restaurant and café category aliases used by planning and cost rules.</summary>
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
            return General;
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

    /// <summary>
    /// Returns the diversity discount group name for the given POI category.
    /// Diminishing marginal utility discount (gamma) is shared across categories belonging to the same group.
    /// Culture and Museum share "Culture &amp; Museum".
    /// Nature and Natural attraction share "Nature &amp; Natural attraction".
    /// Beach, Theme park, Water park, Food &amp; Drink are independent groups.
    /// Null/whitespace category resolves to "General" (exempt from discount).
    /// Unknown non-empty categories return their trimmed canonical name as an independent group.
    /// </summary>
    public static string GetDiversityGroup(string? categoryName)
    {
        var canonical = NormalizeToCanonical(categoryName);
        if (string.Equals(canonical, Culture, StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonical, Museum, StringComparison.OrdinalIgnoreCase))
        {
            return CulturalGroup;
        }

        if (string.Equals(canonical, Nature, StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonical, NaturalAttraction, StringComparison.OrdinalIgnoreCase))
        {
            return NatureGroup;
        }

        return canonical;
    }
}