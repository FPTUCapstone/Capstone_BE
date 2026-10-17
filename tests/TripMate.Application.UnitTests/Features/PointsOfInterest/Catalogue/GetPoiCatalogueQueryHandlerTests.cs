using FluentAssertions;

using TripMate.Application.Features.PointsOfInterest.Catalogue;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Catalogue;

public class GetPoiCatalogueQueryHandlerTests
{
    [Fact]
    public async Task Handle_WithCategoriesAndTags_ReturnsBothListsSortedByName()
    {
        await using var dbContext = TestDbContext.Create();
        dbContext.PoiCategories.AddRange(
            PoiCategory.Create("Restaurant", "Places to eat"),
            PoiCategory.Create("Attraction", null),
            PoiCategory.Create("Museum", null));
        dbContext.Tags.AddRange(
            Tag.Create("Family"),
            Tag.Create("Beach"),
            Tag.Create("Night life"));
        await dbContext.SaveChangesAsync();
        var handler = new GetPoiCatalogueQueryHandler(dbContext);

        var result = await handler.Handle(new GetPoiCatalogueQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Categories.Select(item => item.Name)
            .Should().Equal("Attraction", "Museum", "Restaurant");
        result.Value.Tags.Select(item => item.Name)
            .Should().Equal("Beach", "Family", "Night life");
    }

    [Fact]
    public async Task Handle_ReturnsPersistedIdsForEachOption()
    {
        await using var dbContext = TestDbContext.Create();
        var category = PoiCategory.Create("Attraction", null);
        var tag = Tag.Create("Family");
        dbContext.PoiCategories.Add(category);
        dbContext.Tags.Add(tag);
        await dbContext.SaveChangesAsync();
        var handler = new GetPoiCatalogueQueryHandler(dbContext);

        var result = await handler.Handle(new GetPoiCatalogueQuery(), CancellationToken.None);

        result.Value.Categories.Should().ContainSingle()
            .Which.Should().Be(new PoiCatalogueOptionDto(category.Id, "Attraction"));
        result.Value.Tags.Should().ContainSingle()
            .Which.Should().Be(new PoiCatalogueOptionDto(tag.Id, "Family"));
    }

    [Fact]
    public async Task Handle_WithEqualNames_OrdersByIdSoTheResultIsStable()
    {
        await using var dbContext = TestDbContext.Create();
        var first = Tag.Create("Duplicate");
        var second = Tag.Create("Duplicate");
        dbContext.Tags.AddRange(first, second);
        await dbContext.SaveChangesAsync();
        var handler = new GetPoiCatalogueQueryHandler(dbContext);

        var result = await handler.Handle(new GetPoiCatalogueQuery(), CancellationToken.None);

        result.Value.Tags.Select(item => item.Id)
            .Should().Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Handle_WithNoData_ReturnsEmptyListsNotNull()
    {
        await using var dbContext = TestDbContext.Create();
        var handler = new GetPoiCatalogueQueryHandler(dbContext);

        var result = await handler.Handle(new GetPoiCatalogueQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Categories.Should().NotBeNull().And.BeEmpty();
        result.Value.Tags.Should().NotBeNull().And.BeEmpty();
    }
}