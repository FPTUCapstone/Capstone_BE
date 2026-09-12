using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Explore;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Explore;

public class ExplorePoisQueryHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 2, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_ReturnsOnlyActivePois()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        var activePoi = await SeedPoi(dbContext, category, "Active Park", PointOfInterestStatus.Active);
        var inactivePoi = await SeedPoi(dbContext, category, "Inactive Park", PointOfInterestStatus.Inactive);

        var handler = new ExplorePoisQueryHandler(dbContext);
        var result = await handler.Handle(new ExplorePoisQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle(item => item.Id == activePoi.Id && item.Name == "Active Park");
        result.Value.Items.Should().NotContain(item => item.Id == inactivePoi.Id);
    }

    [Fact]
    public async Task Handle_WithTrimmedCaseInsensitiveSearch_FiltersCorrectly()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        await SeedPoi(dbContext, category, "Da Lat Flower Park", PointOfInterestStatus.Active);
        await SeedPoi(dbContext, category, "My Khe Beach", PointOfInterestStatus.Active);

        var handler = new ExplorePoisQueryHandler(dbContext);
        var query = new ExplorePoisQuery { Search = "  flower  " };
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle(item => item.Name == "Da Lat Flower Park");
    }

    [Fact]
    public async Task Handle_WithCategoryFilter_ReturnsOnlyMatchingCategory()
    {
        await using var dbContext = TestDbContext.Create();
        var category1 = await SeedCategory(dbContext, "Nature");
        var category2 = await SeedCategory(dbContext, "History");
        await SeedPoi(dbContext, category1, "Nature Spot", PointOfInterestStatus.Active);
        await SeedPoi(dbContext, category2, "History Spot", PointOfInterestStatus.Active);

        var handler = new ExplorePoisQueryHandler(dbContext);
        var query = new ExplorePoisQuery { CategoryId = category1.Id };
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle(item => item.Name == "Nature Spot" && item.CategoryId == category1.Id);
    }

    [Fact]
    public async Task Handle_WithUnknownCategory_ReturnsEmptyPageNotError()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Nature");
        await SeedPoi(dbContext, category, "Nature Spot", PointOfInterestStatus.Active);

        var handler = new ExplorePoisQueryHandler(dbContext);
        var query = new ExplorePoisQuery { CategoryId = 99999 };
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WithNoMatches_ReturnsTotalCountZeroTotalPagesZeroEmptyItems()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Nature");
        await SeedPoi(dbContext, category, "Nature Spot", PointOfInterestStatus.Active);

        var handler = new ExplorePoisQueryHandler(dbContext);
        var query = new ExplorePoisQuery { Search = "NonExistent" };
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_NameSort_UsesPoiIdAsFinalTieBreaker()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        var poi1 = await SeedPoi(dbContext, category, "Duplicate Name", PointOfInterestStatus.Active);
        var poi2 = await SeedPoi(dbContext, category, "Duplicate Name", PointOfInterestStatus.Active);

        var handler = new ExplorePoisQueryHandler(dbContext);
        var query = new ExplorePoisQuery { Sort = "name" };
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        var items = result.Value.Items.ToList();
        items[0].Id.Should().BeLessThan(items[1].Id);
    }

    [Fact]
    public async Task Handle_Pagination_BehavesCorrectlyAtPageBoundaries()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        for (int i = 1; i <= 5; i++)
        {
            await SeedPoi(dbContext, category, $"Poi {i}", PointOfInterestStatus.Active);
        }

        var handler = new ExplorePoisQueryHandler(dbContext);

        // Page 1 of size 2
        var page1 = await handler.Handle(new ExplorePoisQuery { Page = 1, PageSize = 2 }, CancellationToken.None);
        page1.Value.TotalCount.Should().Be(5);
        page1.Value.TotalPages.Should().Be(3);
        page1.Value.Items.Should().HaveCount(2);
        page1.Value.Page.Should().Be(1);
        page1.Value.PageSize.Should().Be(2);

        // Page 2 of size 2
        var page2 = await handler.Handle(new ExplorePoisQuery { Page = 2, PageSize = 2 }, CancellationToken.None);
        page2.Value.TotalCount.Should().Be(5);
        page2.Value.TotalPages.Should().Be(3);
        page2.Value.Items.Should().HaveCount(2);
        page2.Value.Page.Should().Be(2);

        // Page 3 of size 2 (last item)
        var page3 = await handler.Handle(new ExplorePoisQuery { Page = 3, PageSize = 2 }, CancellationToken.None);
        page3.Value.TotalCount.Should().Be(5);
        page3.Value.TotalPages.Should().Be(3);
        page3.Value.Items.Should().HaveCount(1);
        page3.Value.Page.Should().Be(3);

        // Page 4 of size 2 (beyond total)
        var page4 = await handler.Handle(new ExplorePoisQuery { Page = 4, PageSize = 2 }, CancellationToken.None);
        page4.Value.TotalCount.Should().Be(5);
        page4.Value.TotalPages.Should().Be(3);
        page4.Value.Items.Should().BeEmpty();
        page4.Value.Page.Should().Be(4);
    }

    [Fact]
    public async Task Handle_DoesNotTrackEntitiesOrWriteAuditLog()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        await SeedPoi(dbContext, category, "Nature Park", PointOfInterestStatus.Active);

        dbContext.ChangeTracker.Clear();

        var handler = new ExplorePoisQueryHandler(dbContext);
        var result = await handler.Handle(new ExplorePoisQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        dbContext.ChangeTracker.Entries().Should().BeEmpty();
        (await dbContext.AuditLogs.CountAsync()).Should().Be(0);
    }

    private static async Task<PoiCategory> SeedCategory(TestDbContext dbContext, string name)
    {
        var category = PoiCategory.Create(name, null);
        dbContext.PoiCategories.Add(category);
        await dbContext.SaveChangesAsync();
        return category;
    }

    private static async Task<PointOfInterest> SeedPoi(
        TestDbContext dbContext,
        PoiCategory category,
        string name,
        PointOfInterestStatus status)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            16.061000m,
            108.246000m,
            1,
            Now,
            "Test Address",
            "Test Description",
            IndoorOutdoorType.Outdoor,
            60,
            false);

        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();

        if (status != PointOfInterestStatus.Active)
        {
            dbContext.Entry(poi).Property(p => p.Status).CurrentValue = status;
            await dbContext.SaveChangesAsync();
        }

        return poi;
    }
}