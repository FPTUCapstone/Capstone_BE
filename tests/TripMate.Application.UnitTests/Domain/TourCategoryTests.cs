using FluentAssertions;

using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Domain;

public sealed class TourCategoryTests
{
    [Fact]
    public void Create_NormalizesStableCodeAndEnglishName()
    {
        var category = TourCategory.Create("  heritage  ", "  Heritage  ");

        category.Code.Should().Be("heritage");
        category.Name.Should().Be("Heritage");
        category.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Heritage")]
    [InlineData("   ", "Heritage")]
    [InlineData("heritage", "")]
    [InlineData("heritage", "   ")]
    public void Create_RejectsMissingRequiredValues(string code, string name)
    {
        var action = () => TourCategory.Create(code, name);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsValuesBeyondApprovedLengths()
    {
        var longCode = new string('a', TourCategory.CodeMaxLength + 1);
        var longName = new string('a', TourCategory.NameMaxLength + 1);
        Action createWithLongCode = () => TourCategory.Create(longCode, "Heritage");
        Action createWithLongName = () => TourCategory.Create("heritage", longName);

        createWithLongCode
            .Should().Throw<ArgumentException>().WithParameterName("code");
        createWithLongName
            .Should().Throw<ArgumentException>().WithParameterName("name");
    }
}