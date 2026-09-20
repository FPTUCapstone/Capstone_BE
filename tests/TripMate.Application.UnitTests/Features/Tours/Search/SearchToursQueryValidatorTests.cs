using FluentAssertions;

using TripMate.Application.Features.Tours.Search;

namespace TripMate.Application.UnitTests.Features.Tours.Search;

public sealed class SearchToursQueryValidatorTests
{
    private readonly SearchToursQueryValidator _validator = new();

    [Fact]
    public async Task Validate_DefaultQuery_IsValid()
    {
        var result = await _validator.ValidateAsync(new SearchToursQuery());

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_Destination_UsesNfcLengthAfterTrim()
    {
        var decomposed = string.Concat(Enumerable.Repeat("e\u0301", 300));
        var valid = await _validator.ValidateAsync(new SearchToursQuery($"  {decomposed}  "));
        var invalid = await _validator.ValidateAsync(new SearchToursQuery(decomposed + "x"));

        valid.IsValid.Should().BeTrue();
        invalid.Errors.Should().ContainSingle(error => error.PropertyName == "Destination");
    }

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(null, -1L)]
    [InlineData(10_000_000_000L, null)]
    [InlineData(null, 10_000_000_000L)]
    [InlineData(200L, 100L)]
    public async Task Validate_InvalidPriceRange_IsRejected(long? minPrice, long? maxPrice)
    {
        var result = await _validator.ValidateAsync(new SearchToursQuery(
            MinPrice: minPrice,
            MaxPrice: maxPrice));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(2147483647, 2)]
    public async Task Validate_InvalidPaging_IsRejected(int page, int pageSize)
    {
        var result = await _validator.ValidateAsync(new SearchToursQuery(
            Page: page,
            PageSize: pageSize));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_MaxDate_IsRejectedBecauseUpperExclusiveCannotBeRepresented()
    {
        var result = await _validator.ValidateAsync(new SearchToursQuery(
            DepartureDate: DateOnly.MaxValue));

        result.Errors.Should().ContainSingle(error => error.PropertyName == "DepartureDate");
    }

    [Fact]
    public async Task Validate_MinDate_IsRejectedBecauseVietnamStartPrecedesUtcYearOne()
    {
        var result = await _validator.ValidateAsync(new SearchToursQuery(
            DepartureDate: DateOnly.MinValue));

        result.Errors.Should().ContainSingle(error => error.PropertyName == "DepartureDate");
    }

    [Fact]
    public void DepartureBounds_UseVietnamCalendarDayAndUtcHalfOpenInterval()
    {
        var succeeded = TourSearchCriteria.TryGetDepartureBoundsUtc(
            new DateOnly(2026, 9, 20),
            out var lower,
            out var upper);

        succeeded.Should().BeTrue();
        lower.Should().Be(new DateTimeOffset(2026, 9, 19, 17, 0, 0, TimeSpan.Zero));
        upper.Should().Be(new DateTimeOffset(2026, 9, 20, 17, 0, 0, TimeSpan.Zero));
    }
}