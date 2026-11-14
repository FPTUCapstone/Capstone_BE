using FluentAssertions;

using TripMate.Application.Features.TourRecommendations.Refresh;

namespace TripMate.Application.UnitTests.Features.TourRecommendations;

public sealed class RefreshTourRecommendationsValidationTests
{
    private readonly RefreshTourRecommendationsCommandValidator _validator = new();

    [Fact]
    public void Validate_AcceptsEmptyOptionalCriteria()
    {
        _validator.Validate(new RefreshTourRecommendationsCommand()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_RejectsReversedDates()
    {
        var result = _validator.Validate(new RefreshTourRecommendationsCommand(
            StartDate: new DateOnly(2026, 12, 2),
            EndDate: new DateOnly(2026, 12, 1)));

        result.Errors.Should().Contain(error => error.PropertyName == "EndDate");
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("10000000000")]
    public void Validate_RejectsInvalidVndBudget(string text)
    {
        decimal value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

        _validator.Validate(new RefreshTourRecommendationsCommand(MinBudget: value))
            .Errors.Should().Contain(error => error.PropertyName == "MinBudget");
    }

    [Fact]
    public void Validate_RejectsReversedBudgetRange()
    {
        _validator.Validate(new RefreshTourRecommendationsCommand(MinBudget: 2m, MaxBudget: 1m))
            .Errors.Should().Contain(error => error.PropertyName == "MaxBudget");
    }

    [Fact]
    public void Validate_UsesTrimmedDestinationLength()
    {
        _validator.Validate(new RefreshTourRecommendationsCommand(
                Destination: $"  {new string('x', 301)}  "))
            .Errors.Should().Contain(error => error.PropertyName == "Destination");

        _validator.Validate(new RefreshTourRecommendationsCommand(
                Destination: $"  {new string('x', 300)}  "))
            .IsValid.Should().BeTrue();
    }
}
