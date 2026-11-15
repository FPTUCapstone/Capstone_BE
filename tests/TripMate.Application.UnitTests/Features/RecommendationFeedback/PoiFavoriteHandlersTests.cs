using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.RecommendationFeedback.Common;
using TripMate.Application.Features.RecommendationFeedback.Favorites;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.RecommendationFeedback;

/// <summary>
/// Traveler feedback UI (spec .scratch/uc10-behavior-learning/feedback-ui-spec.md 3.2, D12): the heart on the
/// place detail page is a per-traveler state that can be turned on and off any number of times.
/// </summary>
public sealed class PoiFavoriteHandlersTests
{
    private const long TravelerId = 11;

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TurningTheHeartOn_SavesTheFavorite()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);

        var result = await Set(db, poi.Id, isFavorite: true);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new PoiFavoriteResponse(poi.Id, true));
        var saved = await db.PoiFavorites.SingleAsync();
        saved.TravelerUserId.Should().Be(TravelerId);
        saved.PointOfInterestId.Should().Be(poi.Id);
        saved.CreatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task TurningTheHeartOnTwice_KeepsOneFavoriteAndItsFirstDate()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        await Set(db, poi.Id, isFavorite: true);

        var again = await Set(db, poi.Id, isFavorite: true, now: Now.AddDays(3));

        again.Value.IsFavorite.Should().BeTrue();
        (await db.PoiFavorites.SingleAsync()).CreatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task TurningTheHeartOn_ForAPlaceThatDoesNotExist_Fails()
    {
        await using var db = CreateDb();

        var result = await Set(db, 404, isFavorite: true);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(FeedbackErrorCodes.PoiNotFound);
        (await db.PoiFavorites.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TurningTheHeartOn_ForAPlaceThatIsNoLongerActive_Fails()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        poi.TryDeactivate(Now);
        await db.SaveChangesAsync();

        var result = await Set(db, poi.Id, isFavorite: true);

        result.ErrorCode.Should().Be(FeedbackErrorCodes.PoiNotFound);
    }

    [Fact]
    public async Task TurningTheHeartOff_RemovesOnlyThisTravelersFavorite()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        await Set(db, poi.Id, isFavorite: true);
        await Set(db, poi.Id, isFavorite: true, travelerId: TravelerId + 1);

        var result = await Set(db, poi.Id, isFavorite: false);

        result.Value.Should().Be(new PoiFavoriteResponse(poi.Id, false));
        (await db.PoiFavorites.SingleAsync()).TravelerUserId.Should().Be(TravelerId + 1);
    }

    [Fact]
    public async Task TurningTheHeartOff_WhenItIsAlreadyOff_Succeeds()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);

        var result = await Set(db, poi.Id, isFavorite: false);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFavorite.Should().BeFalse();
    }

    [Fact]
    public async Task TurningTheHeartOff_StillWorksAfterThePlaceWasDeactivated()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        await Set(db, poi.Id, isFavorite: true);
        poi.TryDeactivate(Now);
        await db.SaveChangesAsync();

        var result = await Set(db, poi.Id, isFavorite: false);

        result.IsSuccess.Should().BeTrue();
        (await db.PoiFavorites.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Get_ReportsTheStateOfTheAskingTravelerOnly()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        await Set(db, poi.Id, isFavorite: true);

        var mine = await Get(db, poi.Id, TravelerId);
        var theirs = await Get(db, poi.Id, TravelerId + 1);

        mine.Value.Should().Be(new PoiFavoriteResponse(poi.Id, true));
        theirs.Value.Should().Be(new PoiFavoriteResponse(poi.Id, false));
    }

    private static Task<Application.Common.Models.Result<PoiFavoriteResponse>> Set(
        ApplicationDbContext db,
        long poiId,
        bool isFavorite,
        long travelerId = TravelerId,
        DateTimeOffset? now = null) =>
        new SetPoiFavoriteCommandHandler(db, new FakeDateTimeProvider { UtcNow = now ?? Now })
            .Handle(new SetPoiFavoriteCommand(travelerId, poiId, isFavorite), CancellationToken.None);

    private static Task<Application.Common.Models.Result<PoiFavoriteResponse>> Get(
        ApplicationDbContext db,
        long poiId,
        long travelerId) =>
        new GetPoiFavoriteQueryHandler(db)
            .Handle(new GetPoiFavoriteQuery(travelerId, poiId), CancellationToken.None);

    private static async Task<PointOfInterest> SeedPoi(ApplicationDbContext db)
    {
        var category = PoiCategory.Create($"Category-{Guid.NewGuid():N}", null);
        var poi = PointOfInterest.Create(category, "Bà Nà Hills", 16m, 108m, 1, Now);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        return poi;
    }

    private static ApplicationDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}