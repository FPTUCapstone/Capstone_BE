using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using TripMate.Application.Features.RecommendationFeedback.Capture;
using TripMate.Application.Features.RecommendationFeedback.Common;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;
using TripMate.Infrastructure.Persistence;

namespace TripMate.Application.UnitTests.Features.RecommendationFeedback;

public sealed class CaptureRecommendationFeedbackCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RecommendationEventType.Like, RecommendationCaptureSource.Explore)]
    [InlineData(RecommendationEventType.Like, RecommendationCaptureSource.PoiDetail)]
    [InlineData(RecommendationEventType.Dislike, RecommendationCaptureSource.Explore)]
    public async Task DirectFeedback_OnActivePoi_AppendsEvent(
        RecommendationEventType eventType,
        RecommendationCaptureSource source)
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);

        var result = await Handle(db, Command(eventType, source, poi.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value.IsReplay.Should().BeFalse();
        result.Value.WasMandatory.Should().BeNull();
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(RecommendationEventType.Like, false)]
    [InlineData(RecommendationEventType.Dislike, true)]
    public async Task ContextualValence_WithUniqueMembership_DerivesMandatory(
        RecommendationEventType eventType,
        bool mandatory)
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        var itinerary = await SeedItinerary(db, 11, (poi.Id, 1, mandatory));

        var result = await Handle(db, Command(
            eventType,
            RecommendationCaptureSource.Itinerary,
            poi.Id,
            itinerary.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value.WasMandatory.Should().Be(mandatory);
    }

    [Fact]
    public async Task DirectFeedback_OnInactivePoi_ReturnsNotFound()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db, active: false);

        var result = await Handle(db, Command(
            RecommendationEventType.Like,
            RecommendationCaptureSource.Explore,
            poi.Id));

        result.ErrorCode.Should().Be(FeedbackErrorCodes.PoiNotFound);
    }

    [Fact]
    public async Task ContextualFeedback_OnInactivePoi_IsAccepted()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db, active: false);
        var itinerary = await SeedItinerary(db, 11, (poi.Id, 1, false));

        var result = await Handle(db, Command(
            RecommendationEventType.Like,
            RecommendationCaptureSource.Itinerary,
            poi.Id,
            itinerary.Id));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task MissingPoi_ReturnsNotFound()
    {
        await using var db = CreateDb();

        var result = await Handle(db, Command(
            RecommendationEventType.Like,
            RecommendationCaptureSource.Explore,
            999));

        result.ErrorCode.Should().Be(FeedbackErrorCodes.PoiNotFound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNotOwnedItinerary_ReturnsNotFound(bool existsForAnotherTraveler)
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        var itineraryId = existsForAnotherTraveler
            ? (await SeedItinerary(db, 99, (poi.Id, 1, false))).Id
            : 999;

        var result = await Handle(db, Command(
            RecommendationEventType.Like,
            RecommendationCaptureSource.Itinerary,
            poi.Id,
            itineraryId));

        result.ErrorCode.Should().Be(FeedbackErrorCodes.ItineraryNotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ContextualValence_WithNonUniqueMembership_ReturnsMismatch(int matches)
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        var other = await SeedPoi(db, name: "Other");
        var items = matches == 0
            ? new[] { (other.Id, 1, false) }
            : new[] { (poi.Id, 1, false), (poi.Id, 2, true) };
        var itinerary = await SeedItinerary(db, 11, items);

        var result = await Handle(db, Command(
            RecommendationEventType.Like,
            RecommendationCaptureSource.Itinerary,
            poi.Id,
            itinerary.Id));

        result.ErrorCode.Should().Be(FeedbackErrorCodes.ContextMismatch);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Skip_RequiresStrictPositionPoiMatch(int requestedPosition, bool succeeds)
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        var itinerary = await SeedItinerary(db, 11, (poi.Id, 1, true));

        var result = await Handle(db, Command(
            RecommendationEventType.Skip,
            RecommendationCaptureSource.Itinerary,
            poi.Id,
            itinerary.Id,
            requestedPosition));

        result.IsSuccess.Should().Be(succeeds);
        if (succeeds)
        {
            result.Value.WasMandatory.Should().BeTrue();
        }
        else
        {
            result.ErrorCode.Should().Be(FeedbackErrorCodes.ContextMismatch);
        }
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(0, 2, false)]
    [InlineData(1, 3, false)]
    [InlineData(1, 1, false)]
    public async Task Reorder_ValidatesRangeAndDistinctPositions(int original, int next, bool succeeds)
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        var other = await SeedPoi(db, name: "Other");
        var itinerary = await SeedItinerary(db, 11, (poi.Id, 2, true), (other.Id, 1, false));

        var result = await Handle(db, Command(
            RecommendationEventType.Reorder,
            RecommendationCaptureSource.Itinerary,
            poi.Id,
            itinerary.Id,
            original,
            next));

        result.IsSuccess.Should().Be(succeeds);
        if (!succeeds)
        {
            result.ErrorCode.Should().Be(FeedbackErrorCodes.ContextMismatch);
        }
    }

    [Fact]
    public async Task IdenticalRetry_ReturnsReplayWithoutRevalidatingInactivePoi()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        var command = Command(RecommendationEventType.Like, RecommendationCaptureSource.Explore, poi.Id);
        (await Handle(db, command)).IsSuccess.Should().BeTrue();
        db.Entry(poi).Property(candidate => candidate.Status).CurrentValue = PointOfInterestStatus.Inactive;
        await db.SaveChangesAsync();

        var retry = await Handle(db, command);

        retry.IsSuccess.Should().BeTrue();
        retry.Value.IsReplay.Should().BeTrue();
        (await db.RecommendationBehaviorEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ReusedTokenWithDifferentInvalidPayload_ReturnsConflictFirst()
    {
        await using var db = CreateDb();
        var poi = await SeedPoi(db);
        var original = Command(RecommendationEventType.Like, RecommendationCaptureSource.Explore, poi.Id);
        (await Handle(db, original)).IsSuccess.Should().BeTrue();
        var conflicting = original with { PoiId = 999 };

        var result = await Handle(db, conflicting);

        result.ErrorCode.Should().Be(FeedbackErrorCodes.EventTokenConflict);
    }

    private static CaptureRecommendationFeedbackCommand Command(
        RecommendationEventType eventType,
        RecommendationCaptureSource source,
        long poiId,
        long? itineraryId = null,
        int? originalPosition = null,
        int? newPosition = null,
        Guid? clientEventId = null) =>
        new(11, clientEventId ?? Guid.NewGuid(), eventType, poiId, itineraryId,
            originalPosition, newPosition, source);

    private static async Task<TripMate.Application.Common.Models.Result<CaptureRecommendationFeedbackResponse>>
        Handle(ApplicationDbContext db, CaptureRecommendationFeedbackCommand command) =>
        await new CaptureRecommendationFeedbackCommandHandler(
            db,
            new FakeDateTimeProvider { UtcNow = Now })
            .Handle(command, CancellationToken.None);

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<PointOfInterest> SeedPoi(
        ApplicationDbContext db,
        bool active = true,
        string name = "POI")
    {
        var category = PoiCategory.Create($"Category-{Guid.NewGuid():N}", null);
        var poi = PointOfInterest.Create(category, name, 10, 106, 1, Now);
        db.PointsOfInterest.Add(poi);
        await db.SaveChangesAsync();
        if (!active)
        {
            db.Entry(poi).Property(candidate => candidate.Status).CurrentValue =
                PointOfInterestStatus.Inactive;
            await db.SaveChangesAsync();
        }

        return poi;
    }

    private static async Task<Itinerary> SeedItinerary(
        ApplicationDbContext db,
        long travelerUserId,
        params (long PoiId, int Sequence, bool Mandatory)[] items)
    {
        var itinerary = Itinerary.CreateManual(travelerUserId, "Test", Itinerary.DraftStatus, Now);
        foreach (var item in items)
        {
            itinerary.AddItem(ItineraryItem.CreateVisit(
                item.Sequence,
                item.PoiId,
                Now.AddHours(item.Sequence),
                Now.AddHours(item.Sequence).AddMinutes(30),
                item.Mandatory,
                null,
                null));
        }

        db.Itineraries.Add(itinerary);
        await db.SaveChangesAsync();
        return itinerary;
    }
}