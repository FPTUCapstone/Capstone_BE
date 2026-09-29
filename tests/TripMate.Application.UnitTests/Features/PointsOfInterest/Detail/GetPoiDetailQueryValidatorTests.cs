using FluentAssertions;

using TripMate.Application.Features.PointsOfInterest.Detail;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Detail;

public class GetPoiDetailQueryValidatorTests
{
    private readonly GetPoiDetailQueryValidator _validator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(long.MaxValue)]
    public void Validate_WithPositiveId_HasNoErrors(long id)
    {
        var query = new GetPoiDetailQuery(id);

        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Validate_WithNonPositiveId_HasErrors(long id)
    {
        var query = new GetPoiDetailQuery(id);

        var result = _validator.Validate(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetPoiDetailQuery.Id));
    }
}