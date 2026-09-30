using FluentAssertions;

using FluentValidation.TestHelper;

using TripMate.Application.Features.RecommendationFeedback.Capture;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.RecommendationFeedback;

public sealed class CaptureRecommendationFeedbackCommandValidatorTests
{
    private readonly CaptureRecommendationFeedbackCommandValidator _validator = new();

    [Theory]
    [InlineData(RecommendationEventType.Like, RecommendationCaptureSource.Explore)]
    [InlineData(RecommendationEventType.Dislike, RecommendationCaptureSource.PoiDetail)]
    public void DirectLikeOrDislike_IsValid(
        RecommendationEventType eventType,
        RecommendationCaptureSource source)
    {
        var result = _validator.TestValidate(Command(eventType: eventType, source: source));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ContextualLike_IsValid()
    {
        var result = _validator.TestValidate(Command(
            eventType: RecommendationEventType.Like,
            source: RecommendationCaptureSource.Itinerary,
            itineraryId: 7));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Skip_WithOriginalPosition_IsValid()
    {
        var result = _validator.TestValidate(Command(
            eventType: RecommendationEventType.Skip,
            source: RecommendationCaptureSource.Itinerary,
            itineraryId: 7,
            originalPosition: 2));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Reorder_WithDistinctPositions_IsValid()
    {
        var result = _validator.TestValidate(Command(
            eventType: RecommendationEventType.Reorder,
            source: RecommendationCaptureSource.Itinerary,
            itineraryId: 7,
            originalPosition: 1,
            newPosition: 2));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Reorder_WithSamePosition_IsShapeValidForHandlerContextValidation()
    {
        var result = _validator.TestValidate(Command(
            eventType: RecommendationEventType.Reorder,
            source: RecommendationCaptureSource.Itinerary,
            itineraryId: 7,
            originalPosition: 1,
            newPosition: 1));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    public void InvalidShape_IsRejected(int variant, bool eventTypeDefined, bool sourceDefined)
    {
        var command = variant == 0
            ? Command(clientEventId: Guid.Empty)
            : Command(
                eventType: eventTypeDefined ? RecommendationEventType.Like : (RecommendationEventType)99,
                source: sourceDefined ? RecommendationCaptureSource.Explore : (RecommendationCaptureSource)99);

        _validator.TestValidate(command).IsValid.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(InvalidCombinations))]
    public void InvalidCombination_IsRejected(CaptureRecommendationFeedbackCommand command)
    {
        _validator.TestValidate(command).IsValid.Should().BeFalse();
    }

    public static TheoryData<CaptureRecommendationFeedbackCommand> InvalidCombinations => new()
    {
        Command(source: RecommendationCaptureSource.Itinerary),
        Command(source: RecommendationCaptureSource.Explore, itineraryId: 7),
        Command(eventType: RecommendationEventType.Like, originalPosition: 1),
        Command(eventType: RecommendationEventType.Dislike, newPosition: 2),
        Command(eventType: RecommendationEventType.Skip, source: RecommendationCaptureSource.Explore,
            originalPosition: 1),
        Command(eventType: RecommendationEventType.Skip, source: RecommendationCaptureSource.Itinerary,
            itineraryId: 7),
        Command(eventType: RecommendationEventType.Skip, source: RecommendationCaptureSource.Itinerary,
            itineraryId: 7, originalPosition: 1, newPosition: 2),
        Command(eventType: RecommendationEventType.Reorder, source: RecommendationCaptureSource.Itinerary,
            itineraryId: 7, originalPosition: 1),
        Command(travelerUserId: 0),
        Command(poiId: 0),
        Command(itineraryId: 0, source: RecommendationCaptureSource.Itinerary),
    };

    private static CaptureRecommendationFeedbackCommand Command(
        long travelerUserId = 11,
        Guid? clientEventId = null,
        RecommendationEventType eventType = RecommendationEventType.Like,
        long poiId = 22,
        long? itineraryId = null,
        int? originalPosition = null,
        int? newPosition = null,
        RecommendationCaptureSource source = RecommendationCaptureSource.Explore) =>
        new(
            travelerUserId,
            clientEventId ?? Guid.NewGuid(),
            eventType,
            poiId,
            itineraryId,
            originalPosition,
            newPosition,
            source);
}