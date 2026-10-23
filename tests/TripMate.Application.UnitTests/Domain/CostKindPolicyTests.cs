using FluentAssertions;

using TripMate.Domain.Common;
using TripMate.Domain.Constants;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class CostKindPolicyTests
{
    [Fact]
    public void FromCategory_FoodAndDrink_IsAverageSpend()
    {
        CostKindPolicy.FromCategory(PoiCategoryNames.FoodAndDrink).Should().Be(CostKind.AverageSpend);
    }

    [Theory]
    [InlineData("food & drink")]
    [InlineData("  FOOD & DRINK ")]
    public void FromCategory_FoodAndDrinkIgnoringCaseAndSpaces_IsAverageSpend(string categoryName)
    {
        CostKindPolicy.FromCategory(categoryName).Should().Be(CostKind.AverageSpend);
    }

    [Theory]
    [InlineData("Museum")]
    [InlineData("Beach")]
    [InlineData("")]
    [InlineData(null)]
    public void FromCategory_AnyOtherCategory_IsAdmissionTicket(string? categoryName)
    {
        CostKindPolicy.FromCategory(categoryName).Should().Be(CostKind.AdmissionTicket);
    }
}