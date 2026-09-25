using FluentAssertions;

using TripMate.Application.Features.PointsOfInterest.Search;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Search;

public class SearchSelectablePoisQueryValidatorTests
{
    private readonly SearchSelectablePoisQueryValidator _validator = new();

    [Fact]
    public void Validate_WithoutLocation_AllowsNameSearch()
    {
        var result = _validator.Validate(new SearchSelectablePoisQuery(
            "Cham",
            null,
            null,
            null,
            1,
            20));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithIncompleteLocation_ReturnsValidationFailure()
    {
        var result = _validator.Validate(new SearchSelectablePoisQuery(
            "Cham",
            16.043m,
            null,
            null,
            1,
            20));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.ErrorMessage == "Latitude, longitude, and radiusKm must be supplied together.");
    }
}