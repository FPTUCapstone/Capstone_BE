using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.RecommendationFeedback.Favorites;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.RecommendationFeedback;

public sealed class ListFavoritePoisQueryHandlerTests
{
    private const long TravelerId = 11;

    // 2026-11-26 04:00 UTC = 2026-11-26 11:00 Vietnam (Thursday = 4).
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WhenTravelerHasNoFavorites_ReturnsEmptyPage()
    {
        await using var db = CreateDb();

        var result = await HandleAsync(db, new ListFavoritePoisQuery(TravelerId));

        result.IsSuccess.Should().BeTrue();
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(ListFavoritePoisQuery.DefaultPageSize);
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnsOnlyActiveFavoritesForAskingTraveler_NewestFirstWithSummaryFields()
    {
        await using var db = CreateDb();
        var (vietnamDay, _) = PoiOpeningState.GetVietnamDayAndTime(Now);

        var heritage = PoiCategory.Create("Heritage", null);
        var beach = PoiCategory.Create("Beach", null);

        var olderPoi = PointOfInterest.Create(
            heritage,
            "Hội An Ancient Town",
            15.8801m,
            108.3380m,
            1,
            Now.AddDays(-10),
            address: "Hội An, Quảng Nam",
            indoorOutdoor: IndoorOutdoorType.Outdoor,
            averageVisitDurationMinutes: 180,
            hasShelter: false);
        olderPoi.AddOpeningHour(PoiOpeningHour.Create(vietnamDay, new TimeOnly(8, 0), new TimeOnly(10, 0), false));

        var newerPoi = PointOfInterest.Create(
            beach,
            "Mỹ Khê Beach",
            16.0598m,
            108.2464m,
            1,
            Now.AddDays(-10),
            address: "Sơn Trà, Đà Nẵng",
            indoorOutdoor: IndoorOutdoorType.Mixed,
            averageVisitDurationMinutes: 120,
            hasShelter: true);
        newerPoi.AddOpeningHour(PoiOpeningHour.Create(vietnamDay, new TimeOnly(6, 0), new TimeOnly(22, 0), false));

        var inactivePoi = PointOfInterest.Create(heritage, "Closed Monument", 16.1m, 108.1m, 1, Now.AddDays(-10));
        inactivePoi.TryDeactivate(Now.AddDays(-1));

        var otherTravelersPoi = PointOfInterest.Create(beach, "Other Traveler Spot", 16.2m, 108.2m, 1, Now.AddDays(-10));

        db.PointsOfInterest.AddRange(olderPoi, newerPoi, inactivePoi, otherTravelersPoi);
        await db.SaveChangesAsync();

        db.PoiPhotos.AddRange(
            PoiPhoto.Create(newerPoi, "https://cdn.tripmate.vn/mykhe-2.jpg", sortOrder: 2),
            PoiPhoto.Create(newerPoi, "https://cdn.tripmate.vn/mykhe-1.jpg", sortOrder: 1));
        db.Reviews.AddRange(
            Review.CreatePoiReview(TravelerId, newerPoi.Id, 5, Now.AddDays(-3)),
            Review.CreatePoiReview(TravelerId + 1, newerPoi.Id, 4, Now.AddDays(-2)));

        db.PoiFavorites.AddRange(
            PoiFavorite.Create(TravelerId, olderPoi.Id, Now.AddHours(-3)),
            PoiFavorite.Create(TravelerId, newerPoi.Id, Now.AddHours(-1)),
            PoiFavorite.Create(TravelerId, inactivePoi.Id, Now),
            PoiFavorite.Create(TravelerId + 1, otherTravelersPoi.Id, Now));
        await db.SaveChangesAsync();

        var result = await HandleAsync(db, new ListFavoritePoisQuery(TravelerId));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.TotalPages.Should().Be(1);
        result.Value.Items.Select(item => item.Id).Should().ContainInOrder(newerPoi.Id, olderPoi.Id);

        var first = result.Value.Items.First();
        first.Name.Should().Be("Mỹ Khê Beach");
        first.CategoryName.Should().Be("Beach");
        first.Address.Should().Be("Sơn Trà, Đà Nẵng");
        first.IndoorOutdoor.Should().Be(IndoorOutdoorType.Mixed);
        first.AverageVisitDurationMinutes.Should().Be(120);
        first.HasShelter.Should().BeTrue();
        first.AverageRating.Should().Be(4.5m);
        first.ReviewCount.Should().Be(2);
        first.ThumbnailUrl.Should().Be("https://cdn.tripmate.vn/mykhe-1.jpg");
        first.IsOpenNow.Should().BeTrue();
        first.FavoritedAtUtc.Should().Be(Now.AddHours(-1));

        var second = result.Value.Items.Last();
        second.IsOpenNow.Should().BeFalse();
        second.AverageRating.Should().BeNull();
        second.ReviewCount.Should().Be(0);
        second.ThumbnailUrl.Should().BeNull();
    }

    [Fact]
    public async Task PaginatesResultsAndReturnsEmptyItemsWhenPageExceedsTotalPages()
    {
        await using var db = CreateDb();
        var category = PoiCategory.Create("Nature", null);
        var pois = Enumerable.Range(1, 3)
            .Select(i => PointOfInterest.Create(category, $"Place {i}", 16m + (i * 0.01m), 108m, 1, Now.AddDays(-5)))
            .ToList();
        db.PointsOfInterest.AddRange(pois);
        await db.SaveChangesAsync();

        db.PoiFavorites.AddRange(
            PoiFavorite.Create(TravelerId, pois[0].Id, Now.AddMinutes(-30)),
            PoiFavorite.Create(TravelerId, pois[1].Id, Now.AddMinutes(-20)),
            PoiFavorite.Create(TravelerId, pois[2].Id, Now.AddMinutes(-10)));
        await db.SaveChangesAsync();

        var page1 = await HandleAsync(db, new ListFavoritePoisQuery(TravelerId, Page: 1, PageSize: 2));
        page1.Value.TotalCount.Should().Be(3);
        page1.Value.TotalPages.Should().Be(2);
        page1.Value.Items.Select(item => item.Id).Should().ContainInOrder(pois[2].Id, pois[1].Id);

        var page2 = await HandleAsync(db, new ListFavoritePoisQuery(TravelerId, Page: 2, PageSize: 2));
        page2.Value.Items.Select(item => item.Id).Should().ContainSingle().Which.Should().Be(pois[0].Id);

        var page3 = await HandleAsync(db, new ListFavoritePoisQuery(TravelerId, Page: 3, PageSize: 2));
        page3.Value.TotalCount.Should().Be(3);
        page3.Value.TotalPages.Should().Be(2);
        page3.Value.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(-1, 12)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public void Validator_RejectsOutOfRangePaging(int page, int pageSize)
    {
        var validator = new ListFavoritePoisQueryValidator();

        var result = validator.Validate(new ListFavoritePoisQuery(TravelerId, page, pageSize));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 12)]
    [InlineData(3, 50)]
    public void Validator_AcceptsValidPaging(int page, int pageSize)
    {
        var validator = new ListFavoritePoisQueryValidator();

        var result = validator.Validate(new ListFavoritePoisQuery(TravelerId, page, pageSize));

        result.IsValid.Should().BeTrue();
    }

    private static Task<Application.Common.Models.Result<PagedFavoritePoisResponse>> HandleAsync(
        ApplicationDbContext db,
        ListFavoritePoisQuery query) =>
        new ListFavoritePoisQueryHandler(db, new FakeDateTimeProvider { UtcNow = Now })
            .Handle(query, CancellationToken.None);

    private static ApplicationDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}