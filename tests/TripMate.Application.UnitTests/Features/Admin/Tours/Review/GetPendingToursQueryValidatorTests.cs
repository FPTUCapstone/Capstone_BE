using FluentAssertions;

using TripMate.Application.Features.Admin.Tours.Review.PendingTours;

namespace TripMate.Application.UnitTests.Features.Admin.Tours.Review;

public sealed class GetPendingToursQueryValidatorTests
{
    private readonly GetPendingToursQueryValidator _validator = new();

    [Fact]
    public void Defaults_AreValid() =>
        _validator.Validate(new GetPendingToursQuery()).IsValid.Should().BeTrue();

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(1_000_001, 20)]
    public void InvalidPaging_IsRejected(int pageNumber, int pageSize) =>
        _validator.Validate(new GetPendingToursQuery(PageNumber: pageNumber, PageSize: pageSize))
            .IsValid.Should().BeFalse();

    [Fact]
    public void KeywordOver100Characters_IsRejected() =>
        _validator.Validate(new GetPendingToursQuery(Keyword: new string('a', 101)))
            .IsValid.Should().BeFalse();

    [Fact]
    public void KeywordOf100Characters_IsAccepted() =>
        _validator.Validate(new GetPendingToursQuery(Keyword: new string('a', 100)))
            .IsValid.Should().BeTrue();
}