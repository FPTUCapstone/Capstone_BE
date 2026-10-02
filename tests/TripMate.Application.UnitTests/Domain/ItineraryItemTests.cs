using FluentAssertions;

using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Domain;

public class ItineraryItemTests
{
    private static readonly DateTimeOffset Arrival = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Departure = new(2026, 10, 20, 3, 30, 0, TimeSpan.Zero);
    private static readonly Action<ItineraryItem, string?> AttachFriendlyExplanation =
        static (item, text) => item.AttachFriendlyExplanation(text);

    [Fact]
    public void CreateRest_WithoutPoi_CreatesTypedBreak()
    {
        var item = ItineraryItem.CreateRest(2, Arrival, Departure, "Free/rest time");

        item.SequenceNo.Should().Be(2);
        item.Kind.Should().Be(ItineraryItemKind.Rest);
        item.PointOfInterestId.Should().BeNull();
        item.IsMandatory.Should().BeFalse();
        item.StayDurationMinutes.Should().Be(30);
        item.RecommendationReason.Should().Be("Free/rest time");
    }

    [Fact]
    public void CreateVisit_WithoutPersistedPoi_Throws()
    {
        var action = () => ItineraryItem.CreateVisit(
            1,
            0,
            Arrival,
            Departure,
            isMandatory: false,
            estimatedCost: 40_000m,
            recommendationReason: "Popular cultural stop");

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateRest_WithNonPositiveDuration_Throws()
    {
        var action = () => ItineraryItem.CreateRest(
            1,
            Departure,
            Arrival,
            "Free/rest time");

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AttachFriendlyExplanation_WithPaddedText_TrimsAndStoresText()
    {
        var item = CreateVisit();

        AttachFriendlyExplanation(item, "  Phù hợp với sở thích văn hóa của bạn.  ");

        item.FriendlyExplanation.Should().Be("Phù hợp với sở thích văn hóa của bạn.");
    }

    [Fact]
    public void AttachFriendlyExplanation_WithWhitespaceOnly_StoresNull()
    {
        var item = CreateVisit();

        AttachFriendlyExplanation(item, "   ");

        item.FriendlyExplanation.Should().BeNull();
    }

    [Fact]
    public void AttachFriendlyExplanation_WithExactlyMaxLength_StoresText()
    {
        var item = CreateVisit();
        var explanation = new string('x', ItineraryItem.FriendlyExplanationMaxLength);

        AttachFriendlyExplanation(item, explanation);

        item.FriendlyExplanation.Should().Be(explanation);
    }

    [Fact]
    public void AttachFriendlyExplanation_ExceedingMaxLength_ThrowsArgumentException()
    {
        var item = CreateVisit();
        var explanation = new string('x', ItineraryItem.FriendlyExplanationMaxLength + 1);

        var action = () => AttachFriendlyExplanation(item, explanation);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AttachFriendlyExplanation_DoesNotChangeRecommendationReason()
    {
        var item = CreateVisit();

        AttachFriendlyExplanation(item, "Phù hợp với sở thích văn hóa của bạn.");

        item.RecommendationReason.Should().Be("Popular cultural stop");
    }

    private static ItineraryItem CreateVisit() =>
        ItineraryItem.CreateVisit(
            1,
            42,
            Arrival,
            Departure,
            isMandatory: false,
            estimatedCost: 40_000m,
            recommendationReason: "Popular cultural stop");
}