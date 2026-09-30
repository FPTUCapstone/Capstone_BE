using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class RecommendationBehaviorEventTests
{
    private static readonly DateTimeOffset OccurredAtUtc =
        new(2026, 9, 29, 2, 0, 0, TimeSpan.Zero);

    public static TheoryData<
        RecommendationEventType,
        RecommendationCaptureSource,
        long?,
        int?,
        int?,
        bool?> ValidShapes => new()
        {
            { RecommendationEventType.Like, RecommendationCaptureSource.Explore, null, null, null, null },
            { RecommendationEventType.Dislike, RecommendationCaptureSource.PoiDetail, null, null, null, null },
            { RecommendationEventType.Skip, RecommendationCaptureSource.Itinerary, 30, 2, null, true },
            { RecommendationEventType.Reorder, RecommendationCaptureSource.Itinerary, 30, 2, 4, false },
        };

    [Theory]
    [MemberData(nameof(ValidShapes))]
    public void Create_WithValidShape_CapturesAppendOnlyEvent(
        RecommendationEventType eventType,
        RecommendationCaptureSource source,
        long? itineraryId,
        int? originalPosition,
        int? newPosition,
        bool? wasMandatory)
    {
        var clientEventId = Guid.Parse("463da9dd-7fb9-4ea6-8447-8b6cc14b16c5");

        var behaviorEvent = RecommendationBehaviorEvent.Create(
            travelerUserId: 10,
            poiId: 20,
            itineraryId,
            eventType,
            originalPosition,
            newPosition,
            wasMandatory,
            source,
            occurredAtUtc: OccurredAtUtc,
            clientEventId);

        behaviorEvent.TravelerUserId.Should().Be(10);
        behaviorEvent.PointOfInterestId.Should().Be(20);
        behaviorEvent.ItineraryId.Should().Be(itineraryId);
        behaviorEvent.EventType.Should().Be(eventType);
        behaviorEvent.OriginalPosition.Should().Be(originalPosition);
        behaviorEvent.NewPosition.Should().Be(newPosition);
        behaviorEvent.WasMandatory.Should().Be(wasMandatory);
        behaviorEvent.Source.Should().Be(source);
        behaviorEvent.OccurredAtUtc.Should().Be(OccurredAtUtc);
        behaviorEvent.ClientEventId.Should().Be(clientEventId);
    }

    [Theory]
    [InlineData(RecommendationCaptureSource.Itinerary, null, true)]
    [InlineData(RecommendationCaptureSource.Itinerary, 30L, null)]
    [InlineData(RecommendationCaptureSource.Explore, 30L, null)]
    [InlineData(RecommendationCaptureSource.PoiDetail, null, false)]
    public void Create_WithInvalidSourceContext_ThrowsArgumentException(
        RecommendationCaptureSource source,
        long? itineraryId,
        bool? wasMandatory)
    {
        var action = () => Create(
            RecommendationEventType.Like,
            source,
            itineraryId,
            originalPosition: null,
            newPosition: null,
            wasMandatory);

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(RecommendationEventType.Skip, RecommendationCaptureSource.Explore)]
    [InlineData(RecommendationEventType.Skip, RecommendationCaptureSource.PoiDetail)]
    [InlineData(RecommendationEventType.Reorder, RecommendationCaptureSource.Explore)]
    [InlineData(RecommendationEventType.Reorder, RecommendationCaptureSource.PoiDetail)]
    public void Create_WithContextRequiredTypeOutsideItinerary_ThrowsArgumentException(
        RecommendationEventType eventType,
        RecommendationCaptureSource source)
    {
        var action = () => Create(
            eventType,
            source,
            itineraryId: null,
            originalPosition: 2,
            newPosition: eventType == RecommendationEventType.Reorder ? 4 : null,
            wasMandatory: null);

        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(RecommendationEventType.Like, 1, null)]
    [InlineData(RecommendationEventType.Like, null, 2)]
    [InlineData(RecommendationEventType.Dislike, 1, null)]
    [InlineData(RecommendationEventType.Dislike, null, 2)]
    [InlineData(RecommendationEventType.Skip, null, null)]
    [InlineData(RecommendationEventType.Skip, 1, 2)]
    [InlineData(RecommendationEventType.Reorder, null, 2)]
    [InlineData(RecommendationEventType.Reorder, 1, null)]
    [InlineData(RecommendationEventType.Reorder, 2, 2)]
    public void Create_WithInvalidPositionShape_ThrowsArgumentException(
        RecommendationEventType eventType,
        int? originalPosition,
        int? newPosition)
    {
        var action = () => Create(
            eventType,
            RecommendationCaptureSource.Itinerary,
            itineraryId: 30,
            originalPosition,
            newPosition,
            wasMandatory: false);

        action.Should().Throw<ArgumentException>();
    }

    private static RecommendationBehaviorEvent Create(
        RecommendationEventType eventType,
        RecommendationCaptureSource source,
        long? itineraryId,
        int? originalPosition,
        int? newPosition,
        bool? wasMandatory) =>
        RecommendationBehaviorEvent.Create(
            travelerUserId: 10,
            poiId: 20,
            itineraryId,
            eventType,
            originalPosition,
            newPosition,
            wasMandatory,
            source,
            occurredAtUtc: OccurredAtUtc,
            clientEventId: Guid.Parse("463da9dd-7fb9-4ea6-8447-8b6cc14b16c5"));
}