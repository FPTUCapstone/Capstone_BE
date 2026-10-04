using FluentAssertions;

using TripMate.Application.Features.CommercialServices.Explore;

namespace TripMate.Application.UnitTests.Features.CommercialServices.Explore;

public sealed class ExploreCommercialServicesQueryValidatorTests
{
    [Theory]
    [InlineData("Vehicle")]
    [InlineData("Hotel")]
    [InlineData("Restaurant")]
    public void Validate_WithSupportedCategory_IsValid(string category)
    {
        var result = new ExploreCommercialServicesQueryValidator().Validate(
            new ExploreCommercialServicesQuery(category, null, 1, 20));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithUnsupportedCategory_ReturnsCategoryError()
    {
        var result = new ExploreCommercialServicesQueryValidator().Validate(
            new ExploreCommercialServicesQuery("Flight", null, 1, 20));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ExploreCommercialServicesQuery.Category));
    }

    [Fact]
    public void Validate_WithPageSizeOutsidePublicContract_ReturnsPageSizeError()
    {
        var result = new ExploreCommercialServicesQueryValidator().Validate(
            new ExploreCommercialServicesQuery(null, null, 1, 101));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(ExploreCommercialServicesQuery.PageSize));
    }
}