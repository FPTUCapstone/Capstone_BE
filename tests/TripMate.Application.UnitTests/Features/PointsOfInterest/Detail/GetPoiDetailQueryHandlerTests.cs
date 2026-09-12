using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.PointsOfInterest.Common;
using TripMate.Application.Features.PointsOfInterest.Detail;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.PointsOfInterest.Detail;

public class GetPoiDetailQueryHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 2, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Handle_WithActivePoi_ReturnsAllApprovedCoreFieldsWithoutCreatorOrAccountData()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Historic");
        var poi = await SeedPoi(dbContext, category, "Imperial City", PointOfInterestStatus.Active);

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new GetPoiDetailQuery(poi.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;

        dto.Id.Should().Be(poi.Id);
        dto.Name.Should().Be("Imperial City");
        dto.Description.Should().Be("Test Description");
        dto.Status.Should().Be(PointOfInterestStatus.Active);
        dto.CategoryId.Should().Be(category.Id);
        dto.CategoryName.Should().Be("Historic");
        dto.Latitude.Should().Be(16.061000m);
        dto.Longitude.Should().Be(108.246000m);
        dto.Address.Should().Be("Test Address");
        dto.IndoorOutdoor.Should().Be(IndoorOutdoorType.Outdoor);
        dto.AverageVisitDurationMinutes.Should().Be(60);
        dto.HasShelter.Should().BeFalse();
        dto.CreatedAtUtc.Should().Be(Now);
        dto.UpdatedAtUtc.Should().Be(Now);

        // Prove creator/account data is not exposed on detail DTO
        typeof(PoiDetailDto).GetProperty("CreatedById").Should().BeNull();
        typeof(PoiDetailDto).GetProperty("CreatedBy").Should().BeNull();
        typeof(PoiDetailDto).GetProperty("User").Should().BeNull();
    }

    [Fact]
    public async Task Handle_WithNonExistentPoi_ReturnsPoiNotFound()
    {
        await using var dbContext = TestDbContext.Create();
        var handler = CreateHandler(dbContext);

        var result = await handler.Handle(new GetPoiDetailQuery(999999), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PoiErrorCodes.NotFound);
        result.ErrorMessage.Should().Be(PoiErrorMessages.NotFound);
    }

    [Fact]
    public async Task Handle_WithInactivePoi_ReturnsPoiNotFound()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Historic");
        var inactivePoi = await SeedPoi(dbContext, category, "Inactive Sight", PointOfInterestStatus.Inactive);

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new GetPoiDetailQuery(inactivePoi.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(PoiErrorCodes.NotFound);
        result.ErrorMessage.Should().Be(PoiErrorMessages.NotFound);
    }

    [Fact]
    public async Task Handle_Ordering_OrdersOpeningHoursByDayPhotosBySortOrderThenIdAndTagsByNameThenId()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Cultural");
        var poi = await SeedPoi(dbContext, category, "Heritage Museum", PointOfInterestStatus.Active);

        // Add opening hours out of order: day 5 (Fri), day 0 (Sun), day 2 (Tue)
        poi.AddOpeningHour(PoiOpeningHour.Create(5, new TimeOnly(9, 0), new TimeOnly(17, 0), false));
        poi.AddOpeningHour(PoiOpeningHour.Create(0, new TimeOnly(8, 0), new TimeOnly(16, 0), false));
        poi.AddOpeningHour(PoiOpeningHour.Create(2, new TimeOnly(9, 0), new TimeOnly(18, 0), false));

        // Add tags out of alphabetical order
        var tagZoo = Tag.Create("Zoo");
        var tagBeach = Tag.Create("Beach");
        var tagAlpine = Tag.Create("Alpine");
        dbContext.Tags.AddRange(tagZoo, tagBeach, tagAlpine);
        await dbContext.SaveChangesAsync();

        poi.AddTag(tagZoo);
        poi.AddTag(tagBeach);
        poi.AddTag(tagAlpine);

        // Add photos with sort order and ID ties
        var photoA = PoiPhoto.Create(poi, "https://example.com/a.jpg", "caption A", sortOrder: 2);
        var photoB = PoiPhoto.Create(poi, "https://example.com/b.jpg", "caption B", sortOrder: 1);
        var photoC = PoiPhoto.Create(poi, "https://example.com/c.jpg", "caption C", sortOrder: 1);
        dbContext.PoiPhotos.AddRange(photoA, photoB, photoC);

        await dbContext.SaveChangesAsync();

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new GetPoiDetailQuery(poi.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // Opening hours ordered by DayOfWeek (0, 2, 5)
        result.Value.OpeningHours.Select(h => h.DayOfWeek).Should().Equal(0, 2, 5);

        // Tags ordered by Name ("Alpine", "Beach", "Zoo")
        result.Value.Tags.Select(t => t.Name).Should().Equal("Alpine", "Beach", "Zoo");

        // Photos ordered by sortOrder then ID: photoB (sort 1, id lower), photoC (sort 1, id higher), photoA (sort 2)
        result.Value.Photos.Select(p => p.Url).Should().Equal(
            "https://example.com/b.jpg",
            "https://example.com/c.jpg",
            "https://example.com/a.jpg");
    }

    [Fact]
    public async Task Handle_CalculatedMetrics_MatchesListRulesForRatingAndIsOpenNow()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");

        // Wednesday 2026-09-09 03:00 UTC = 10:00 Vietnam time (day 3)
        var wednesday10Am = new DateTimeOffset(2026, 9, 9, 3, 0, 0, TimeSpan.Zero);
        var poi = await SeedPoi(dbContext, category, "Pagoda", PointOfInterestStatus.Active);
        poi.AddOpeningHour(PoiOpeningHour.Create(3, new TimeOnly(8, 0), new TimeOnly(17, 0), false));

        // Reviews: 4, 4, 5 for "POI" -> avg 4.333... -> rounded 4.3, count 3
        dbContext.Reviews.AddRange(
            Review.Create(1, "POI", poi.Id, 4, Now),
            Review.Create(2, "POI", poi.Id, 4, Now),
            Review.Create(3, "POI", poi.Id, 5, Now));

        // Review for "Tour" -> excluded
        dbContext.Reviews.Add(Review.Create(4, "Tour", poi.Id, 5, Now));

        await dbContext.SaveChangesAsync();

        var handler = CreateHandler(dbContext, wednesday10Am);
        var result = await handler.Handle(new GetPoiDetailQuery(poi.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AverageRating.Should().Be(4.3m);
        result.Value.ReviewCount.Should().Be(3);
        result.Value.IsOpenNow.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithNullableFieldsNullAndEmptyCollections_PreservesNullsAndEmptyCollections()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        var poi = PointOfInterest.Create(
            category,
            "Minimal POI",
            16.061000m,
            108.246000m,
            1,
            Now,
            address: null,
            description: null,
            IndoorOutdoorType.Outdoor,
            60,
            false);

        dbContext.PointsOfInterest.Add(poi);
        await dbContext.SaveChangesAsync();

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new GetPoiDetailQuery(poi.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;

        dto.Description.Should().BeNull();
        dto.Address.Should().BeNull();
        dto.ScenicScore.Should().BeNull();
        dto.PhotoRating.Should().BeNull();
        dto.AverageRating.Should().BeNull();
        dto.ReviewCount.Should().Be(0);
        dto.IsOpenNow.Should().BeFalse();
        dto.OpeningHours.Should().BeEmpty();
        dto.Photos.Should().BeEmpty();
        dto.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_DoesNotTrackEntitiesOrWriteAuditLog()
    {
        await using var dbContext = TestDbContext.Create();
        var category = await SeedCategory(dbContext, "Attraction");
        var poi = await SeedPoi(dbContext, category, "Track Check", PointOfInterestStatus.Active);

        dbContext.ChangeTracker.Clear();

        var handler = CreateHandler(dbContext);
        var result = await handler.Handle(new GetPoiDetailQuery(poi.Id), CancellationToken.None);

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

    private static GetPoiDetailQueryHandler CreateHandler(
        TestDbContext dbContext,
        DateTimeOffset? utcNow = null) =>
        new(dbContext, new FakeDateTimeProvider { UtcNow = utcNow ?? Now });
}