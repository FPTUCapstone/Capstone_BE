using FluentAssertions;

using TripMate.Domain.Constants;

namespace TripMate.Application.UnitTests.Common;

public class PoiCategoryNamesTests
{
    [Theory]
    [InlineData("Food & Drink")]
    [InlineData("food & drink")]
    [InlineData("FOOD & DRINK")]
    [InlineData("Food and Drink")]
    [InlineData("Ẩm thực")]
    [InlineData("ẩm thực")]
    [InlineData("Ăn uống")]
    [InlineData("ăn uống")]
    [InlineData("Restaurant")]
    [InlineData("Nhà hàng")]
    [InlineData("Quán ăn")]
    public void IsFoodAndDrink_WhenMatchingSupportedFoodCategories_ReturnsTrue(string categoryName)
    {
        PoiCategoryNames.IsFoodAndDrink(categoryName).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Theme park")]
    [InlineData("Hotel")]
    [InlineData("Attraction")]
    [InlineData("Natural attraction")]
    public void IsFoodAndDrink_WhenNotFoodCategory_ReturnsFalse(string? categoryName)
    {
        PoiCategoryNames.IsFoodAndDrink(categoryName).Should().BeFalse();
    }
}
