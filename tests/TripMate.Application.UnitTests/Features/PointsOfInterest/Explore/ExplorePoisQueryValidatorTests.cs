using FluentAssertions;

using TripMate.Application.Features.PointsOfInterest.Explore;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Explore;

public class ExplorePoisQueryValidatorTests
{
    private readonly ExplorePoisQueryValidator _validator = new();

    // --- Defaults ---

    [Fact]
    public void Validate_WithDefaults_HasNoErrors()
    {
        var result = _validator.Validate(new ExplorePoisQuery());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var query = new ExplorePoisQuery();

        query.Page.Should().Be(1);
        query.PageSize.Should().Be(20);
        query.OpenNow.Should().BeFalse();
        query.Sort.Should().Be("name");
    }

    // --- Search ---

    [Fact]
    public void Validate_WithSearchAtMaxLength_HasNoErrors()
    {
        var query = new ExplorePoisQuery { Search = new string('a', 200) };

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithTrimmedSearchExceedingMaxLength_HasErrors()
    {
        var query = new ExplorePoisQuery { Search = new string('a', 201) };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ExplorePoisQuery.Search));
    }

    [Fact]
    public void Validate_WithPaddedSearchWithinMaxLengthAfterTrimming_HasNoErrors()
    {
        var query = new ExplorePoisQuery { Search = $"  {new string('a', 200)}  " };

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    // --- CategoryId ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveCategoryId_HasErrors(int categoryId)
    {
        var query = new ExplorePoisQuery { CategoryId = categoryId };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ExplorePoisQuery.CategoryId));
    }

    [Fact]
    public void Validate_WithPositiveCategoryId_HasNoErrors()
    {
        var query = new ExplorePoisQuery { CategoryId = 1 };

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithNullCategoryId_HasNoErrors()
    {
        var query = new ExplorePoisQuery { CategoryId = null };

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    // --- Coordinates ---

    [Theory]
    [InlineData(-90.000001)]
    [InlineData(90.000001)]
    public void Validate_WithLatitudeOutOfRange_HasErrors(decimal latitude)
    {
        var query = new ExplorePoisQuery
        {
            OriginLatitude = latitude,
            OriginLongitude = 108.0m,
        };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ExplorePoisQuery.OriginLatitude));
    }

    [Theory]
    [InlineData(-180.000001)]
    [InlineData(180.000001)]
    public void Validate_WithLongitudeOutOfRange_HasErrors(decimal longitude)
    {
        var query = new ExplorePoisQuery
        {
            OriginLatitude = 16.0m,
            OriginLongitude = longitude,
        };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ExplorePoisQuery.OriginLongitude));
    }

    [Fact]
    public void Validate_WithLatitudeOnlyMissingLongitude_HasErrors()
    {
        var query = new ExplorePoisQuery { OriginLatitude = 16.0m, OriginLongitude = null };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ExplorePoisQuery.OriginLongitude));
    }

    [Fact]
    public void Validate_WithLongitudeOnlyMissingLatitude_HasErrors()
    {
        var query = new ExplorePoisQuery { OriginLatitude = null, OriginLongitude = 108.0m };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ExplorePoisQuery.OriginLatitude));
    }

    [Fact]
    public void Validate_WithBothCoordinates_HasNoErrors()
    {
        var query = new ExplorePoisQuery { OriginLatitude = 16.0m, OriginLongitude = 108.0m };

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithBothCoordinatesNull_HasNoErrors()
    {
        var query = new ExplorePoisQuery
        {
            OriginLatitude = null,
            OriginLongitude = null,
        };

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    // --- MaxDistanceKm ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveMaxDistance_HasErrors(int maxDistanceKm)
    {
        var query = new ExplorePoisQuery
        {
            OriginLatitude = 16.0m,
            OriginLongitude = 108.0m,
            MaxDistanceKm = maxDistanceKm,
        };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ExplorePoisQuery.MaxDistanceKm));
    }

    [Fact]
    public void Validate_WithMaxDistanceWithoutOrigin_HasErrors()
    {
        var query = new ExplorePoisQuery { MaxDistanceKm = 10.0m };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithMaxDistanceAndOrigin_HasNoErrors()
    {
        var query = new ExplorePoisQuery
        {
            OriginLatitude = 16.0m,
            OriginLongitude = 108.0m,
            MaxDistanceKm = 10.0m,
        };

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    // --- Sort ---

    [Theory]
    [InlineData("name")]
    [InlineData("distance")]
    [InlineData("rating")]
    public void Validate_WithValidSort_HasNoErrors(string sort)
    {
        var query = new ExplorePoisQuery { Sort = sort };

        // distance sort requires origin, but sort-value validation is separate
        if (sort == "distance")
        {
            query = query with { OriginLatitude = 16.0m, OriginLongitude = 108.0m };
        }

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("Name")]
    [InlineData("DISTANCE")]
    [InlineData("")]
    public void Validate_WithInvalidSort_HasErrors(string sort)
    {
        var query = new ExplorePoisQuery { Sort = sort };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ExplorePoisQuery.Sort));
    }

    [Fact]
    public void Validate_WithDistanceSortWithoutOrigin_HasErrors()
    {
        var query = new ExplorePoisQuery { Sort = "distance" };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
    }

    // --- Page ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositivePage_HasErrors(int page)
    {
        var query = new ExplorePoisQuery { Page = page };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ExplorePoisQuery.Page));
    }

    // --- PageSize ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_WithPageSizeOutOfRange_HasErrors(int pageSize)
    {
        var query = new ExplorePoisQuery { PageSize = pageSize };

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ExplorePoisQuery.PageSize));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Validate_WithPageSizeAtBoundary_HasNoErrors(int pageSize)
    {
        var query = new ExplorePoisQuery { PageSize = pageSize };

        _validator.Validate(query).IsValid.Should().BeTrue();
    }
}