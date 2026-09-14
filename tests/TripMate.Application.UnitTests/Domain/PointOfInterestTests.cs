using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class PointOfInterestTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithValidValues_NormalizesValuesAndAppliesDefaults()
    {
        var category = PoiCategory.Create("Natural landmark", null);

        var poi = PointOfInterest.Create(
            category,
            "  Marble Mountains  ",
            16.0038915m,
            108.2641704m,
            17,
            Now,
            "  81 Huyen Tran Cong Chua  ",
            "  Limestone hills  ");

        poi.Category.Should().BeSameAs(category);
        poi.Name.Should().Be("Marble Mountains");
        poi.Latitude.Should().Be(16.003892m);
        poi.Longitude.Should().Be(108.264170m);
        poi.Address.Should().Be("81 Huyen Tran Cong Chua");
        poi.Description.Should().Be("Limestone hills");
        poi.IndoorOutdoor.Should().Be(IndoorOutdoorType.Outdoor);
        poi.AverageVisitDurationMinutes.Should()
            .Be(PointOfInterest.DefaultAverageVisitDurationMinutes);
        poi.HasShelter.Should().BeFalse();
        poi.ScenicScore.Should().BeNull();
        poi.PhotoRating.Should().BeNull();
        poi.Status.Should().Be(PointOfInterestStatus.Active);
        poi.CreatedById.Should().Be(17);
        poi.CreatedAtUtc.Should().Be(Now);
        poi.UpdatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_WithPaddedFieldsAtSqlLimits_NormalizesValues()
    {
        var category = PoiCategory.Create("Natural landmark", null);
        var expectedName = new string('n', PointOfInterest.NameMaxLength);
        var expectedAddress = new string('a', PointOfInterest.AddressMaxLength);
        var expectedDescription = new string('d', PointOfInterest.DescriptionMaxLength);

        var poi = PointOfInterest.Create(
            category,
            $"  {expectedName}  ",
            16,
            108,
            17,
            Now,
            $"  {expectedAddress}  ",
            $"  {expectedDescription}  ");

        poi.Name.Should().Be(expectedName);
        poi.Address.Should().Be(expectedAddress);
        poi.Description.Should().Be(expectedDescription);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("address")]
    [InlineData("description")]
    public void Create_WithFieldLongerThanTrimmedSqlLimit_Throws(string parameterName)
    {
        var category = PoiCategory.Create("Natural landmark", null);
        var name = "Marble Mountains";
        string? address = null;
        string? description = null;

        switch (parameterName)
        {
            case "name":
                name = $"  {new string('n', PointOfInterest.NameMaxLength + 1)}  ";
                break;
            case "address":
                address = $"  {new string('a', PointOfInterest.AddressMaxLength + 1)}  ";
                break;
            case "description":
                description = $"  {new string('d', PointOfInterest.DescriptionMaxLength + 1)}  ";
                break;
        }

        var action = () => PointOfInterest.Create(
            category,
            name,
            16,
            108,
            17,
            Now,
            address,
            description);

        action.Should()
            .Throw<ArgumentException>()
            .Which.ParamName.Should().Be(parameterName);
    }

    [Fact]
    public void Create_WithWhitespaceOnlyOptionalFields_NormalizesToNull()
    {
        var category = PoiCategory.Create("Natural landmark", null);

        var poi = PointOfInterest.Create(
            category,
            "Marble Mountains",
            16,
            108,
            17,
            Now,
            new string(' ', PointOfInterest.AddressMaxLength + 1),
            new string(' ', PointOfInterest.DescriptionMaxLength + 1));

        poi.Address.Should().BeNull();
        poi.Description.Should().BeNull();
    }

    [Theory]
    [InlineData(90.000001, 108.0)]
    [InlineData(-90.000001, 108.0)]
    [InlineData(16.0, 180.000001)]
    [InlineData(16.0, -180.000001)]
    public void Create_WithCoordinatesOutsideRange_Throws(double latitude, double longitude)
    {
        var category = PoiCategory.Create("Natural landmark", null);

        var action = () => PointOfInterest.Create(
            category,
            "Marble Mountains",
            (decimal)latitude,
            (decimal)longitude,
            17,
            Now);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_Throws(string name)
    {
        var category = PoiCategory.Create("Natural landmark", null);

        var action = () => PointOfInterest.Create(category, name, 16, 108, 17, Now);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithNonPositiveVisitDuration_Throws()
    {
        var category = PoiCategory.Create("Natural landmark", null);

        var action = () => PointOfInterest.Create(
            category,
            "Marble Mountains",
            16,
            108,
            17,
            Now,
            averageVisitDurationMinutes: 0);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddOpeningHour_WithDuplicateDay_Throws()
    {
        var category = PoiCategory.Create("Natural landmark", null);
        var poi = PointOfInterest.Create(category, "Marble Mountains", 16, 108, 17, Now);
        poi.AddOpeningHour(PoiOpeningHour.Create(0, new TimeOnly(7, 0), new TimeOnly(17, 30), false));

        var action = () => poi.AddOpeningHour(
            PoiOpeningHour.Create(0, new TimeOnly(8, 0), new TimeOnly(18, 0), false));

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddTag_WithSameTagTwice_DoesNotDuplicateMapping()
    {
        var category = PoiCategory.Create("Natural landmark", null);
        var tag = Tag.Create("Nature");
        var poi = PointOfInterest.Create(category, "Marble Mountains", 16, 108, 17, Now);

        poi.AddTag(tag);
        poi.AddTag(tag);

        poi.PoiTags.Should().ContainSingle();
    }
}