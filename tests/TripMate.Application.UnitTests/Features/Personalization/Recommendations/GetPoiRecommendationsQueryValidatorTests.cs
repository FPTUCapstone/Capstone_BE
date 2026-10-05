using FluentAssertions;

using TripMate.Application.Features.Personalization.Recommendations;

namespace TripMate.Application.UnitTests.Features.Personalization.Recommendations;

public class GetPoiRecommendationsQueryValidatorTests
{
    private readonly GetPoiRecommendationsQueryValidator _validator = new();

    [Theory]
    [InlineData(-90, -180, 1, 1)]
    [InlineData(90, 180, 50, 20)]
    public void Validate_WithBoundaryValues_HasNoErrors(
        int latitude,
        int longitude,
        int searchRadiusKm,
        int limit)
    {
        var query = CreateQuery(latitude, longitude, searchRadiusKm, limit);

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithNullLimit_HasNoErrors()
    {
        var query = CreateQuery(limit: null);

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(-90.000001)]
    [InlineData(90.000001)]
    public void Validate_WithLatitudeOutOfRange_HasErrors(decimal latitude)
    {
        var result = _validator.Validate(CreateQuery(latitude: latitude));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(GetPoiRecommendationsQuery.ExplorationLatitude));
    }

    [Theory]
    [InlineData(-180.000001)]
    [InlineData(180.000001)]
    public void Validate_WithLongitudeOutOfRange_HasErrors(decimal longitude)
    {
        var result = _validator.Validate(CreateQuery(longitude: longitude));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(GetPoiRecommendationsQuery.ExplorationLongitude));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_WithSearchRadiusOutOfRange_HasErrors(int searchRadiusKm)
    {
        var result = _validator.Validate(CreateQuery(searchRadiusKm: searchRadiusKm));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(GetPoiRecommendationsQuery.SearchRadiusKm));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Validate_WithLimitOutOfRange_HasErrors(int limit)
    {
        var result = _validator.Validate(CreateQuery(limit: limit));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.PropertyName == nameof(GetPoiRecommendationsQuery.Limit));
    }

    private static GetPoiRecommendationsQuery CreateQuery(
        decimal latitude = 16.0471m,
        decimal longitude = 108.2068m,
        int searchRadiusKm = 10,
        int? limit = 10) =>
        new(
            TravelerUserId: 42,
            ExplorationLatitude: latitude,
            ExplorationLongitude: longitude,
            SearchRadiusKm: searchRadiusKm,
            Limit: limit);
}
