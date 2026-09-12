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

        var handler = CreateHandler(dbContext);
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

        var handler = CreateHandler(dbContext);
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

        var handler = CreateHandler(dbContext);
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

        var handler = CreateHandler(dbContext);
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

        var handler = CreateHandler(dbContext);
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

        var handler = CreateHandler(dbContext);
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

        var handler = CreateHandler(dbContext);

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

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new ExplorePoisQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        dbContext.ChangeTracker.Entries().Should().BeEmpty();
        (await dbContext.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithOriginCoordinates_CalculatesDistanceWithTwoDecimalRoundingAndNormalizesInput()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        var poiZero = await SeedPoi(dbContext, category, "Zero Distance", PointOfInterestStatus.Active, 16.061000m, 108.246000m);
        var poiRef = await SeedPoi(dbContext, category, "Reference Distance", PointOfInterestStatus.Active, 16.071000m, 108.246000m);

        var handler = CreateHandler(dbContext);
        // Input has 7 decimal places, should normalize to 6 decimals
        var query = new ExplorePoisQuery
        {
            OriginLatitude = 16.0610004m,
            OriginLongitude = 108.2460004m,
        };

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var zeroItem = result.Value.Items.Single(item => item.Id == poiZero.Id);
        var refItem = result.Value.Items.Single(item => item.Id == poiRef.Id);

        zeroItem.DistanceKm.Should().Be(0.00m);
        refItem.DistanceKm.Should().Be(1.11m);
    }

    [Fact]
    public async Task Handle_WithoutOriginCoordinates_ReturnsNullDistance()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        var poi = await SeedPoi(dbContext, category, "Park", PointOfInterestStatus.Active, 16.061000m, 108.246000m);

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new ExplorePoisQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Single().DistanceKm.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WithMaxDistanceKm_AppliesUnroundedFilterAndBoundaryInclusion()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        // Distance from (16.061, 108.246) to (16.071, 108.246) is unrounded ~1.11195 km
        var poi = await SeedPoi(dbContext, category, "Reference", PointOfInterestStatus.Active, 16.071000m, 108.246000m);

        var handler = CreateHandler(dbContext);

        // Filter slightly below unrounded distance (1.11 < 1.11195) -> excluded
        var queryExcluded = new ExplorePoisQuery
        {
            OriginLatitude = 16.061000m,
            OriginLongitude = 108.246000m,
            MaxDistanceKm = 1.11m,
        };
        var resultExcluded = await handler.Handle(queryExcluded, CancellationToken.None);
        resultExcluded.Value.Items.Should().BeEmpty();

        // Filter slightly above unrounded distance (1.112 > 1.11195) -> included
        var queryIncluded = new ExplorePoisQuery
        {
            OriginLatitude = 16.061000m,
            OriginLongitude = 108.246000m,
            MaxDistanceKm = 1.112m,
        };
        var resultIncluded = await handler.Handle(queryIncluded, CancellationToken.None);
        resultIncluded.Value.Items.Should().ContainSingle(item => item.Id == poi.Id);

        // Filter at exact unrounded boundary -> included (boundary inclusion <=)
        var queryBoundary = new ExplorePoisQuery
        {
            OriginLatitude = 16.061000m,
            OriginLongitude = 108.246000m,
            MaxDistanceKm = 1.111951m,
        };
        var resultBoundary = await handler.Handle(queryBoundary, CancellationToken.None);
        resultBoundary.Value.Items.Should().ContainSingle(item => item.Id == poi.Id);
    }

    [Fact]
    public async Task Handle_WithDistanceSort_OrdersByUnroundedDistanceAscendingThenNameThenId()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        var poiFar = await SeedPoi(dbContext, category, "Far", PointOfInterestStatus.Active, 16.081000m, 108.246000m);
        var poiNear2 = await SeedPoi(dbContext, category, "Near B", PointOfInterestStatus.Active, 16.062000m, 108.246000m);
        var poiNear1 = await SeedPoi(dbContext, category, "Near A", PointOfInterestStatus.Active, 16.062000m, 108.246000m);

        var handler = CreateHandler(dbContext);
        var query = new ExplorePoisQuery
        {
            OriginLatitude = 16.061000m,
            OriginLongitude = 108.246000m,
            Sort = "distance",
        };

        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var itemIds = result.Value.Items.Select(item => item.Id).ToList();
        itemIds.Should().Equal(poiNear1.Id, poiNear2.Id, poiFar.Id);
    }

    [Fact]
    public async Task Handle_OpeningHours_ProjectsIsOpenNowAndFiltersWhenOpenNowTrue()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");

        // Wednesday 2026-09-09 03:00 UTC = 10:00 Vietnam time (day 3)
        var wednesday10Am = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);

        // POI 1: Open Wednesday 08:00 - 17:00 -> Open
        var poiOpen = await SeedPoi(dbContext, category, "Open POI", PointOfInterestStatus.Active);
        poiOpen.AddOpeningHour(PoiOpeningHour.Create(3, new TimeOnly(8, 0), new TimeOnly(17, 0), isClosed: false));

        // POI 2: Open Wednesday 12:00 - 17:00 -> Not yet open
        var poiNotYetOpen = await SeedPoi(dbContext, category, "Later POI", PointOfInterestStatus.Active);
        poiNotYetOpen.AddOpeningHour(PoiOpeningHour.Create(3, new TimeOnly(12, 0), new TimeOnly(17, 0), isClosed: false));

        // POI 3: Wednesday isClosed = true -> Closed
        var poiClosedDay = await SeedPoi(dbContext, category, "Closed POI", PointOfInterestStatus.Active);
        poiClosedDay.AddOpeningHour(PoiOpeningHour.Create(3, null, null, isClosed: true));

        // POI 4: No hours for Wednesday -> Closed
        var poiNoHours = await SeedPoi(dbContext, category, "No Hours POI", PointOfInterestStatus.Active);

        await dbContext.SaveChangesAsync();

        var handler = CreateHandler(dbContext, wednesday10Am);

        // Without openNow filter
        var resultAll = await handler.Handle(new ExplorePoisQuery { OpenNow = false }, CancellationToken.None);
        resultAll.Value.TotalCount.Should().Be(4);
        resultAll.Value.Items.Single(item => item.Id == poiOpen.Id).IsOpenNow.Should().BeTrue();
        resultAll.Value.Items.Single(item => item.Id == poiNotYetOpen.Id).IsOpenNow.Should().BeFalse();
        resultAll.Value.Items.Single(item => item.Id == poiClosedDay.Id).IsOpenNow.Should().BeFalse();
        resultAll.Value.Items.Single(item => item.Id == poiNoHours.Id).IsOpenNow.Should().BeFalse();

        // With openNow = true
        var resultFiltered = await handler.Handle(new ExplorePoisQuery { OpenNow = true }, CancellationToken.None);
        resultFiltered.Value.TotalCount.Should().Be(1);
        resultFiltered.Value.Items.Should().ContainSingle(item => item.Id == poiOpen.Id && item.IsOpenNow);
    }

    [Fact]
    public async Task Handle_Reviews_ProjectsAverageRatingWithOneDecimalRoundingAndReviewCount()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");

        var poi1 = await SeedPoi(dbContext, category, "POI 1", PointOfInterestStatus.Active);
        var poi2 = await SeedPoi(dbContext, category, "POI 2", PointOfInterestStatus.Active);
        var poi3 = await SeedPoi(dbContext, category, "POI 3", PointOfInterestStatus.Active);
        var poi4 = await SeedPoi(dbContext, category, "POI 4", PointOfInterestStatus.Active);

        // POI 1: ratings 4 and 5 -> avg 4.5, count 2
        dbContext.Reviews.AddRange(
            Review.Create(1, "POI", poi1.Id, 4, Now),
            Review.Create(2, "POI", poi1.Id, 5, Now));

        // POI 2: ratings 4, 4, 5 -> avg 4.333... -> rounds to 4.3, count 3
        dbContext.Reviews.AddRange(
            Review.Create(1, "POI", poi2.Id, 4, Now),
            Review.Create(2, "POI", poi2.Id, 4, Now),
            Review.Create(3, "POI", poi2.Id, 5, Now));

        // POI 3: rating 5 for "Tour" (polymorphic, not POI) -> should NOT count
        dbContext.Reviews.Add(Review.Create(1, "Tour", poi3.Id, 5, Now));

        // POI 4: no reviews -> avg null, count 0

        await dbContext.SaveChangesAsync();

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new ExplorePoisQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var item1 = result.Value.Items.Single(item => item.Id == poi1.Id);
        var item2 = result.Value.Items.Single(item => item.Id == poi2.Id);
        var item3 = result.Value.Items.Single(item => item.Id == poi3.Id);
        var item4 = result.Value.Items.Single(item => item.Id == poi4.Id);

        item1.AverageRating.Should().Be(4.5m);
        item1.ReviewCount.Should().Be(2);

        item2.AverageRating.Should().Be(4.3m);
        item2.ReviewCount.Should().Be(3);

        item3.AverageRating.Should().BeNull();
        item3.ReviewCount.Should().Be(0);

        item4.AverageRating.Should().BeNull();
        item4.ReviewCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_WithRatingSort_OrdersRatedFirstDescendingThenReviewCountThenNameThenId()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");

        var poiA = await SeedPoi(dbContext, category, "Alpha", PointOfInterestStatus.Active);
        var poiB = await SeedPoi(dbContext, category, "Beta", PointOfInterestStatus.Active);
        var poiC = await SeedPoi(dbContext, category, "Charlie", PointOfInterestStatus.Active);
        var poiD = await SeedPoi(dbContext, category, "Delta", PointOfInterestStatus.Active);

        // poiA: avg 4.5, count 2
        dbContext.Reviews.AddRange(
            Review.Create(1, "POI", poiA.Id, 4, Now),
            Review.Create(2, "POI", poiA.Id, 5, Now));

        // poiB: avg 4.3, count 3
        dbContext.Reviews.AddRange(
            Review.Create(1, "POI", poiB.Id, 4, Now),
            Review.Create(2, "POI", poiB.Id, 4, Now),
            Review.Create(3, "POI", poiB.Id, 5, Now));

        // poiC: avg 4.5, count 10 (same avg as poiA, but higher review count!)
        for (int i = 0; i < 5; i++)
        {
            dbContext.Reviews.Add(Review.Create(1, "POI", poiC.Id, 4, Now));
            dbContext.Reviews.Add(Review.Create(2, "POI", poiC.Id, 5, Now));
        }

        // poiD: no reviews (unrated)

        await dbContext.SaveChangesAsync();

        var handler = CreateHandler(dbContext);
        var query = new ExplorePoisQuery { Sort = "rating" };
        var result = await handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var ids = result.Value.Items.Select(item => item.Id).ToList();
        // Rating sort: poiC (4.5, count 10), then poiA (4.5, count 2), then poiB (4.3, count 3), then poiD (unrated)
        ids.Should().Equal(poiC.Id, poiA.Id, poiB.Id, poiD.Id);
    }

    [Fact]
    public async Task Handle_Photos_ProjectsFirstThumbnailBySortOrderThenPhotoId()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");

        var poiWithPhotos = await SeedPoi(dbContext, category, "With Photos", PointOfInterestStatus.Active);
        var poiNoPhotos = await SeedPoi(dbContext, category, "No Photos", PointOfInterestStatus.Active);

        // Photo 1: sortOrder 2
        dbContext.PoiPhotos.Add(PoiPhoto.Create(poiWithPhotos, "https://example.com/2.jpg", "caption", 2));
        // Photo 2: sortOrder 1, photoId will be assigned
        var photo2 = PoiPhoto.Create(poiWithPhotos, "https://example.com/1a.jpg", "caption", 1);
        var photo3 = PoiPhoto.Create(poiWithPhotos, "https://example.com/1b.jpg", "caption", 1);
        dbContext.PoiPhotos.AddRange(photo2, photo3);

        await dbContext.SaveChangesAsync();

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new ExplorePoisQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var withPhotosItem = result.Value.Items.Single(item => item.Id == poiWithPhotos.Id);
        var noPhotosItem = result.Value.Items.Single(item => item.Id == poiNoPhotos.Id);

        // photo2 was added first so its Id is lower than photo3
        withPhotosItem.ThumbnailUrl.Should().Be("https://example.com/1a.jpg");
        noPhotosItem.ThumbnailUrl.Should().BeNull();
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
        PointOfInterestStatus status,
        decimal latitude = 16.061000m,
        decimal longitude = 108.246000m)
    {
        var poi = PointOfInterest.Create(
            category,
            name,
            latitude,
            longitude,
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

    private static ExplorePoisQueryHandler CreateHandler(
        TestDbContext dbContext,
        DateTimeOffset? utcNow = null) =>
        new(dbContext, new FakeDateTimeProvider { UtcNow = utcNow ?? Now });
}