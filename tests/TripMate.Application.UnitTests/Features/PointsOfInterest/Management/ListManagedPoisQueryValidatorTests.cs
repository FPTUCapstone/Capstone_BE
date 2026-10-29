using FluentAssertions;

using TripMate.Application.Features.PointsOfInterest.Management;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Management;

public class ListManagedPoisQueryValidatorTests
{
    private readonly ListManagedPoisQueryValidator _validator = new();

    [Fact]
    public void Validate_Defaults_AcceptsFirstPageWithBothStatuses()
    {
        var query = new ListManagedPoisQuery();
        query.Page.Should().Be(1);
        query.PageSize.Should().Be(20);
        query.Status.Should().BeNull();
        query.CategoryId.Should().BeNull();
        _validator.Validate(query).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Validate_NonPositivePage_RejectsPage(int page)
    {
        _validator.Validate(new ListManagedPoisQuery { Page = page }).Errors
            .Should().Contain(error => error.PropertyName == nameof(ListManagedPoisQuery.Page));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Validate_PageSizeOutsideBounds_RejectsPageSize(int pageSize)
    {
        _validator.Validate(new ListManagedPoisQuery { PageSize = pageSize }).Errors
            .Should().Contain(error => error.PropertyName == nameof(ListManagedPoisQuery.PageSize));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Validate_PageSizeAtBounds_Accepts(int pageSize)
    {
        _validator.Validate(new ListManagedPoisQuery { PageSize = pageSize }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Active")]
    [InlineData("Inactive")]
    public void Validate_ApprovedStatus_Accepts(string? status)
    {
        _validator.Validate(new ListManagedPoisQuery { Status = status }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("active")]
    [InlineData("INACTIVE")]
    [InlineData(" Active ")]
    [InlineData("1")]
    [InlineData("Deleted")]
    public void Validate_OtherStatus_RejectsExactStatus(string status)
    {
        _validator.Validate(new ListManagedPoisQuery { Status = status }).Errors
            .Should().Contain(error => error.PropertyName == nameof(ListManagedPoisQuery.Status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveCategory_RejectsCategory(int categoryId)
    {
        _validator.Validate(new ListManagedPoisQuery { CategoryId = categoryId }).Errors
            .Should().Contain(error => error.PropertyName == nameof(ListManagedPoisQuery.CategoryId));
    }

    [Fact]
    public void Validate_PositiveCategoryAtInt32Maximum_AcceptsShape()
    {
        _validator.Validate(new ListManagedPoisQuery { CategoryId = int.MaxValue }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_SearchLimitAfterTrim_AcceptsPaddingAndWhitespaceOnly()
    {
        _validator.Validate(new ListManagedPoisQuery { Search = "  " + new string('a', 200) + "\t " }).IsValid.Should().BeTrue();
        _validator.Validate(new ListManagedPoisQuery { Search = new string(' ', 250) }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_SearchOverLimitAfterTrim_RejectsSearch(bool useSurrogatePairs)
    {
        var search = useSurrogatePairs ? string.Concat(Enumerable.Repeat("\U0001F30D", 101)) : new string('a', 201);
        _validator.Validate(new ListManagedPoisQuery { Search = " " + search + " " }).Errors
            .Should().Contain(error => error.PropertyName == nameof(ListManagedPoisQuery.Search));
    }

    [Theory]
    [InlineData(1, int.MaxValue)]
    [InlineData(2, 1_073_741_824)]
    [InlineData(100, 21_474_837)]
    public void Validate_RepresentableOffset_Accepts(int pageSize, int page)
    {
        _validator.Validate(new ListManagedPoisQuery { Page = page, PageSize = pageSize }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(2, 1_073_741_825)]
    [InlineData(100, 21_474_838)]
    [InlineData(100, int.MaxValue)]
    public void Validate_OffsetBeyondInt32_RejectsWithoutThrowing(int pageSize, int page)
    {
        _validator.Validate(new ListManagedPoisQuery { Page = page, PageSize = pageSize }).Errors
            .Should().Contain(error => error.PropertyName == nameof(ListManagedPoisQuery.Page));
    }
}
