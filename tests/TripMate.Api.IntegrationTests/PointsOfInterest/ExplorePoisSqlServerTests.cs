using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.PointsOfInterest.Detail;
using TripMate.Application.Features.PointsOfInterest.Explore;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Api.IntegrationTests.PointsOfInterest;

[Collection(nameof(TripMateApiFactory))]
public sealed class ExplorePoisSqlServerTests
{
    private static readonly DateTimeOffset SundayTestTime =
        new(2026, 9, 13, 3, 0, 0, TimeSpan.Zero); // 10:00 AM UTC+07:00 Sunday

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_WithVietnameseNameSearch_PerformsCaseInsensitiveMatchUnderCollation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        await using var context = database.CreateDbContext();
        var handler = CreateExploreHandler(context);

        // Lowercase contains
        var lowerResult = await handler.Handle(new ExplorePoisQuery { Search = "mỹ khê" }, default);
        lowerResult.IsSuccess.Should().BeTrue();
        lowerResult.Value.TotalCount.Should().Be(1);
        lowerResult.Value.Items.Should().ContainSingle(p => p.Id == seed.MyKheId);

        // Uppercase contains
        var upperResult = await handler.Handle(new ExplorePoisQuery { Search = "MỸ KHÊ" }, default);
        upperResult.IsSuccess.Should().BeTrue();
        upperResult.Value.TotalCount.Should().Be(1);
        upperResult.Value.Items.Should().ContainSingle(p => p.Id == seed.MyKheId);

        // Trimmed whitespace search
        var trimmedResult = await handler.Handle(new ExplorePoisQuery { Search = "   Bãi biển Mỹ Khê   " }, default);
        trimmedResult.IsSuccess.Should().BeTrue();
        trimmedResult.Value.TotalCount.Should().Be(1);
        trimmedResult.Value.Items.Should().ContainSingle(p => p.Id == seed.MyKheId);

        // Substring match
        var partialResult = await handler.Handle(new ExplorePoisQuery { Search = "biển" }, default);
        partialResult.IsSuccess.Should().BeTrue();
        partialResult.Value.TotalCount.Should().Be(1);
        partialResult.Value.Items.Should().ContainSingle(p => p.Id == seed.MyKheId);

        // Non-existent search
        var nonExistentResult = await handler.Handle(new ExplorePoisQuery { Search = "Địa điểm không tồn tại" }, default);
        nonExistentResult.IsSuccess.Should().BeTrue();
        nonExistentResult.Value.TotalCount.Should().Be(0);
        nonExistentResult.Value.Items.Should().BeEmpty();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_WithDistanceFilterAndSorting_ComputesHaversineAndAppliesOrderingOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        await using var context = database.CreateDbContext();
        var handler = CreateExploreHandler(context);

        // Origin at Da Nang center: 16.0544, 108.2022
        // Radius 10 km should only include My Khe (~4.44 km) and exclude Ba Na (~24.78 km) & Hoi An (~24.88 km)
        var distanceFilterResult = await handler.Handle(
            new ExplorePoisQuery
            {
                OriginLatitude = 16.0544m,
                OriginLongitude = 108.2022m,
                MaxDistanceKm = 10m,
            },
            default);

        distanceFilterResult.IsSuccess.Should().BeTrue();
        distanceFilterResult.Value.TotalCount.Should().Be(1);
        distanceFilterResult.Value.Items.Should().ContainSingle(p => p.Id == seed.MyKheId);
        distanceFilterResult.Value.Items.First().DistanceKm.Should().Be(4.45m);

        // Distance sort order: closest first (My Khe ~4.44 km, Ba Na ~24.78 km, Hoi An ~24.88 km)
        var distanceSortResult = await handler.Handle(
            new ExplorePoisQuery
            {
                OriginLatitude = 16.0544m,
                OriginLongitude = 108.2022m,
                Sort = "distance",
            },
            default);

        distanceSortResult.IsSuccess.Should().BeTrue();
        distanceSortResult.Value.Items.Select(p => p.Id).Should().Equal(seed.MyKheId, seed.BaNaId, seed.HoiAnId);

        // Name sort order: Ba Na, My Khe, Hoi An
        var nameSortResult = await handler.Handle(
            new ExplorePoisQuery { Sort = "name" },
            default);

        nameSortResult.IsSuccess.Should().BeTrue();
        nameSortResult.Value.Items.Select(p => p.Id).Should().Equal(seed.BaNaId, seed.MyKheId, seed.HoiAnId);

        // Rating sort order: My Khe (4.5), Ba Na (4.0), Hoi An (null rating at end)
        var ratingSortResult = await handler.Handle(
            new ExplorePoisQuery { Sort = "rating" },
            default);

        ratingSortResult.IsSuccess.Should().BeTrue();
        ratingSortResult.Value.Items.Select(p => p.Id).Should().Equal(seed.MyKheId, seed.BaNaId, seed.HoiAnId);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_WithRatingAggregationAndThumbnail_ProjectsExpectedMetricsOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        await using var context = database.CreateDbContext();
        var handler = CreateExploreHandler(context);

        var result = await handler.Handle(new ExplorePoisQuery { Sort = "name" }, default);

        result.IsSuccess.Should().BeTrue();
        var items = result.Value.Items.ToDictionary(p => p.Id);

        // My Khe: ratings 5 and 4 -> 4.5 average, 2 reviews; photo with SortOrder 0
        items[seed.MyKheId].AverageRating.Should().Be(4.5m);
        items[seed.MyKheId].ReviewCount.Should().Be(2);
        items[seed.MyKheId].ThumbnailUrl.Should().Be("https://example.com/mykhe-0.jpg");

        // Ba Na: rating 4 -> 4.0 average, 1 review; photo
        items[seed.BaNaId].AverageRating.Should().Be(4.0m);
        items[seed.BaNaId].ReviewCount.Should().Be(1);
        items[seed.BaNaId].ThumbnailUrl.Should().Be("https://example.com/bana.jpg");

        // Hoi An: no reviews, no photos
        items[seed.HoiAnId].AverageRating.Should().BeNull();
        items[seed.HoiAnId].ReviewCount.Should().Be(0);
        items[seed.HoiAnId].ThumbnailUrl.Should().BeNull();
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_WithOpenNow_FiltersByCurrentVietnamScheduleOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        await using var context = database.CreateDbContext();
        var handler = CreateExploreHandler(context);

        // Sunday 10:00 AM UTC+07:00:
        // My Khe: open 06:00 - 18:00 (Open)
        // Ba Na: closed on Sunday (Closed)
        // Hoi An: open 00:00 - 23:59 (Open)
        var result = await handler.Handle(new ExplorePoisQuery { OpenNow = true }, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Select(p => p.Id).Should().BeEquivalentTo([seed.MyKheId, seed.HoiAnId]);
        result.Value.Items.Should().OnlyContain(p => p.IsOpenNow);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_WithPaginationAndActiveStatus_PaginatesDeterministicallyAndExcludesInactiveOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        await using var context = database.CreateDbContext();
        var handler = CreateExploreHandler(context);

        // Inactive POI should never be returned, TotalCount = 3 active POIs
        var page1Result = await handler.Handle(
            new ExplorePoisQuery { Page = 1, PageSize = 2, Sort = "name" },
            default);

        page1Result.IsSuccess.Should().BeTrue();
        page1Result.Value.Page.Should().Be(1);
        page1Result.Value.PageSize.Should().Be(2);
        page1Result.Value.TotalCount.Should().Be(3);
        page1Result.Value.TotalPages.Should().Be(2);
        page1Result.Value.Items.Should().HaveCount(2);
        page1Result.Value.Items.Select(p => p.Id).Should().Equal(seed.BaNaId, seed.MyKheId);

        var page2Result = await handler.Handle(
            new ExplorePoisQuery { Page = 2, PageSize = 2, Sort = "name" },
            default);

        page2Result.IsSuccess.Should().BeTrue();
        page2Result.Value.Page.Should().Be(2);
        page2Result.Value.PageSize.Should().Be(2);
        page2Result.Value.TotalCount.Should().Be(3);
        page2Result.Value.TotalPages.Should().Be(2);
        page2Result.Value.Items.Should().HaveCount(1);
        page2Result.Value.Items.First().Id.Should().Be(seed.HoiAnId);

        // Inactive POI is not in any page
        page1Result.Value.Items.Should().NotContain(p => p.Id == seed.InactivePoiId);
        page2Result.Value.Items.Should().NotContain(p => p.Id == seed.InactivePoiId);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_GetDetail_ReturnsOrderedChildrenAndRatingSummaryOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        await using var context = database.CreateDbContext();
        var handler = CreateDetailHandler(context);

        // Valid Active POI detail
        var myKheResult = await handler.Handle(new GetPoiDetailQuery(seed.MyKheId), default);

        myKheResult.IsSuccess.Should().BeTrue();
        var detail = myKheResult.Value;
        detail.Id.Should().Be(seed.MyKheId);
        detail.Name.Should().Be("Bãi biển Mỹ Khê");
        detail.CategoryName.Should().Be("Beach");
        detail.AverageRating.Should().Be(4.5m);
        detail.ReviewCount.Should().Be(2);
        detail.IsOpenNow.Should().BeTrue();

        // Photos ordered by SortOrder then Id
        var photos = detail.Photos.ToArray();
        photos.Should().HaveCount(2);
        photos[0].SortOrder.Should().Be(0);
        photos[0].Url.Should().Be("https://example.com/mykhe-0.jpg");
        photos[1].SortOrder.Should().Be(1);
        photos[1].Url.Should().Be("https://example.com/mykhe-1.jpg");

        // Opening hours ordered by DayOfWeek 0..6
        var hours = detail.OpeningHours.ToArray();
        hours.Should().HaveCount(7);
        for (var day = 0; day < 7; day++)
        {
            hours[day].DayOfWeek.Should().Be(day);
            hours[day].OpenTime.Should().Be(new TimeOnly(6, 0));
            hours[day].CloseTime.Should().Be(new TimeOnly(18, 0));
            hours[day].IsClosed.Should().BeFalse();
        }

        // Tags ordered by Name then Id
        var tags = detail.Tags.ToArray();
        tags.Should().HaveCount(2);
        tags[0].Name.Should().Be("Family");
        tags[1].Name.Should().Be("Nature");

        // Inactive POI detail returns 404
        var inactiveResult = await handler.Handle(new GetPoiDetailQuery(seed.InactivePoiId), default);
        inactiveResult.IsSuccess.Should().BeFalse();
        inactiveResult.ErrorCode.Should().Be(PoiErrorCodes.NotFound);

        // Missing POI detail returns 404
        var missingResult = await handler.Handle(new GetPoiDetailQuery(999_999L), default);
        missingResult.IsSuccess.Should().BeFalse();
        missingResult.ErrorCode.Should().Be(PoiErrorCodes.NotFound);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_QueryBoundsAndNoNPlusOne_ProvesBoundedExecutionOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        var interceptor = new TestCommandCounterInterceptor();
        await using var context = database.CreateDbContext(interceptor);

        // 1. Explore list query: exactly 1 count query + 1 bounded page query = 2 commands total
        interceptor.Reset();
        var exploreHandler = CreateExploreHandler(context);
        var listResult = await exploreHandler.Handle(new ExplorePoisQuery { Page = 1, PageSize = 20 }, default);

        listResult.IsSuccess.Should().BeTrue();
        interceptor.CommandCount.Should().Be(2,
            "explore list must execute exactly 1 count query and 1 bounded result query with no N+1 subqueries");

        // 2. Detail query: fixed query count (3 queries) independent of child rows
        interceptor.Reset();
        var detailHandler = CreateDetailHandler(context);
        var detailResult = await detailHandler.Handle(new GetPoiDetailQuery(seed.MyKheId), default);

        detailResult.IsSuccess.Should().BeTrue();
        var myKheQueryCount = interceptor.CommandCount;
        myKheQueryCount.Should().Be(3,
            "detail query must execute exactly 3 queries (POI aggregate, photos, reviews)");

        // 3. Detail query on Ba Na (different number of photos/reviews) executes the exact same query count
        interceptor.Reset();
        var baNaResult = await detailHandler.Handle(new GetPoiDetailQuery(seed.BaNaId), default);

        baNaResult.IsSuccess.Should().BeTrue();
        interceptor.CommandCount.Should().Be(myKheQueryCount,
            "detail query count must be fixed and not depend on the number of children");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_ListAndDetail_PreservesReadOnlyStateAndCreatesNoAuditOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        var countsBefore = await database.ReadExplorationCountsAsync();

        await using (var context = database.CreateDbContext())
        {
            var exploreHandler = CreateExploreHandler(context);
            var listResult = await exploreHandler.Handle(new ExplorePoisQuery(), default);
            listResult.IsSuccess.Should().BeTrue();

            var detailHandler = CreateDetailHandler(context);
            var detailResult = await detailHandler.Handle(new GetPoiDetailQuery(seed.MyKheId), default);
            detailResult.IsSuccess.Should().BeTrue();
        }

        var countsAfter = await database.ReadExplorationCountsAsync();
        countsAfter.Should().Be(countsBefore,
            "browsing POIs must be completely read-only: no POI, tag, review, photo, or audit record may be added or modified");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Handle_WithCancelledToken_AbortsWithoutPartialResponseOnSqlServer()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync();
        var seed = await SeedExplorationDataAsync(database);

        await using var context = database.CreateDbContext();
        var exploreHandler = CreateExploreHandler(context);
        var detailHandler = CreateDetailHandler(context);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var actList = () => exploreHandler.Handle(new ExplorePoisQuery(), cts.Token);
        await actList.Should().ThrowAsync<OperationCanceledException>();

        var actDetail = () => detailHandler.Handle(new GetPoiDetailQuery(seed.MyKheId), cts.Token);
        await actDetail.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ExplorePoisQueryHandler CreateExploreHandler(ApplicationDbContext context) =>
        new(context, new TestDateTimeProvider(SundayTestTime));

    private static GetPoiDetailQueryHandler CreateDetailHandler(ApplicationDbContext context) =>
        new(context, new TestDateTimeProvider(SundayTestTime));

    private static async Task<ExplorationSeedResult> SeedExplorationDataAsync(SqlServerTestDatabase database)
    {
        await using var context = database.CreateDbContext();

        var admin = new User
        {
            Email = "sql-explore-admin@example.com",
            FullName = "SQL Exploration Test Admin",
            Role = UserRole.Administrator,
            Status = AccountStatus.Active,
            CreatedAtUtc = SundayTestTime,
            UpdatedAtUtc = SundayTestTime,
        };

        var traveler = new User
        {
            Email = "sql-explore-traveler@example.com",
            FullName = "SQL Exploration Test Traveler",
            Role = UserRole.Traveler,
            Status = AccountStatus.Active,
            CreatedAtUtc = SundayTestTime,
            UpdatedAtUtc = SundayTestTime,
        };

        var beachCat = PoiCategory.Create("Beach", "Sun and sand");
        var cultureCat = PoiCategory.Create("Culture", "Historical and cultural landmarks");

        var tagNature = Tag.Create("Nature");
        var tagFamily = Tag.Create("Family");
        var tagRelax = Tag.Create("Relaxation");

        context.Users.AddRange(admin, traveler);
        context.PoiCategories.AddRange(beachCat, cultureCat);
        context.Tags.AddRange(tagNature, tagFamily, tagRelax);
        await context.SaveChangesAsync();

        // POI 1: Bãi biển Mỹ Khê (Beach, Lat 16.0592, Lon 108.2435, Da Nang)
        var myKhe = PointOfInterest.Create(
            beachCat,
            "Bãi biển Mỹ Khê",
            16.0592m,
            108.2435m,
            admin.Id,
            SundayTestTime,
            "Vo Nguyen Giap, Da Nang",
            "Famous beach in Da Nang",
            IndoorOutdoorType.Outdoor,
            120,
            false);

        for (var day = 0; day < 7; day++)
        {
            myKhe.AddOpeningHour(PoiOpeningHour.Create((byte)day, new TimeOnly(6, 0), new TimeOnly(18, 0), false));
        }
        myKhe.AddTag(tagNature);
        myKhe.AddTag(tagFamily);

        // POI 2: BÀ NÀ HILLS (Culture, Lat 15.9988, Lon 107.9961, Da Nang)
        var baNa = PointOfInterest.Create(
            cultureCat,
            "BÀ NÀ HILLS",
            15.9988m,
            107.9961m,
            admin.Id,
            SundayTestTime,
            "Hoa Vang, Da Nang",
            "Mountain resort and theme park",
            IndoorOutdoorType.Mixed,
            240,
            true);

        // Ba Na is closed on Sunday (Day 0), open Mon-Sat 08:00 - 17:00
        baNa.AddOpeningHour(PoiOpeningHour.Create(0, null, null, true));
        for (var day = 1; day < 7; day++)
        {
            baNa.AddOpeningHour(PoiOpeningHour.Create((byte)day, new TimeOnly(8, 0), new TimeOnly(17, 0), false));
        }
        baNa.AddTag(tagFamily);

        // POI 3: Phố cổ Hội An (Culture, Lat 15.8801, Lon 108.3380, Hoi An)
        var hoiAn = PointOfInterest.Create(
            cultureCat,
            "Phố cổ Hội An",
            15.8801m,
            108.3380m,
            admin.Id,
            SundayTestTime,
            "Hoi An, Quang Nam",
            "Ancient town UNESCO heritage",
            IndoorOutdoorType.Outdoor,
            180,
            true);

        for (var day = 0; day < 7; day++)
        {
            hoiAn.AddOpeningHour(PoiOpeningHour.Create((byte)day, new TimeOnly(0, 0), new TimeOnly(23, 59), false));
        }
        hoiAn.AddTag(tagNature);
        hoiAn.AddTag(tagRelax);

        // POI 4: Hidden Inactive Spot (Beach, Inactive)
        var inactivePoi = PointOfInterest.Create(
            beachCat,
            "Hidden Inactive Spot",
            16.0000m,
            108.0000m,
            admin.Id,
            SundayTestTime,
            "Secret Location",
            "Inactive test spot",
            IndoorOutdoorType.Outdoor,
            60,
            false);

        for (var day = 0; day < 7; day++)
        {
            inactivePoi.AddOpeningHour(PoiOpeningHour.Create((byte)day, new TimeOnly(8, 0), new TimeOnly(17, 0), false));
        }

        context.PointsOfInterest.AddRange(myKhe, baNa, hoiAn, inactivePoi);
        await context.SaveChangesAsync();

        // Update inactivePoi status directly in SQL
        await database.ExecuteNonQueryAsync(
            $"UPDATE catalog.POIs SET status = 'Inactive' WHERE poi_id = {inactivePoi.Id};");

        // Photos
        var photo1 = PoiPhoto.Create(myKhe, "https://example.com/mykhe-1.jpg", "Beach View", 1);
        var photo2 = PoiPhoto.Create(myKhe, "https://example.com/mykhe-0.jpg", "Sunrise", 0);
        var photo3 = PoiPhoto.Create(baNa, "https://example.com/bana.jpg", "Golden Bridge", 0);

        context.PoiPhotos.AddRange(photo1, photo2, photo3);

        // Reviews
        var review1 = Review.Create(traveler.Id, "POI", myKhe.Id, 5, SundayTestTime, 5, 5, "Amazing beach!");
        var review2 = Review.Create(traveler.Id, "POI", myKhe.Id, 4, SundayTestTime, 4, 4, "Good beach!");
        var review3 = Review.Create(traveler.Id, "POI", baNa.Id, 4, SundayTestTime, 4, 4, "Nice view!");

        context.Reviews.AddRange(review1, review2, review3);
        await context.SaveChangesAsync();

        return new ExplorationSeedResult(
            myKhe.Id,
            baNa.Id,
            hoiAn.Id,
            inactivePoi.Id,
            beachCat.Id,
            cultureCat.Id);
    }

    private sealed record ExplorationSeedResult(
        long MyKheId,
        long BaNaId,
        long HoiAnId,
        long InactivePoiId,
        int BeachCategoryId,
        int CultureCategoryId);

    private sealed class TestDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}