using FluentAssertions;

using TripMate.Application.Features.Scheduling.Explanation;
using TripMate.Domain.Entities;
using TripMate.Domain.Enums;

namespace TripMate.Application.UnitTests.Features.Scheduling.Explanation;

public sealed class ItineraryExplanationFallbackTests
{
    [Theory]
    [InlineData(
        ItineraryItemKind.Visit,
        true,
        42L,
        "Điểm đến bắt buộc theo yêu cầu của bạn.")]
    [InlineData(
        ItineraryItemKind.Visit,
        false,
        42L,
        "Điểm gợi ý phù hợp với sở thích và khung giờ của bạn.")]
    [InlineData(
        ItineraryItemKind.Rest,
        false,
        42L,
        "Điểm nghỉ ngơi được đề xuất trên lộ trình.")]
    [InlineData(
        ItineraryItemKind.Rest,
        false,
        null,
        "Thời gian nghỉ ngơi tự do giữa các điểm đến.")]
    public void Resolve_ReturnsExactApprovedText(
        ItineraryItemKind kind,
        bool isMandatory,
        long? poiId,
        string expected)
    {
        var result = ItineraryExplanationFallback.Resolve(kind, isMandatory, poiId);

        result.Should().Be(expected);
    }

    [Fact]
    public void Constants_DoNotExceedFriendlyExplanationLimit()
    {
        var values = new[]
        {
            ItineraryExplanationFallback.MandatoryVisit,
            ItineraryExplanationFallback.OptionalVisit,
            ItineraryExplanationFallback.RestWithPoi,
            ItineraryExplanationFallback.RestWithoutPoi,
        };

        values.Should().OnlyContain(
            value => value.Length <= ItineraryItem.FriendlyExplanationMaxLength);
    }

    [Fact]
    public void SameContext_IsStableAcrossCallsAndBuilderInstances()
    {
        var item = Item("Museum", "Culture", isMandatory: true, poiId: 42);

        ItineraryExplanationFallback.Resolve(item)
            .Should().Be(ItineraryExplanationFallback.Resolve(item with { }));
    }

    [Fact]
    public void ContextSelectsTravelLanguageWithoutChangingRecommendationReason()
    {
        var item = Item("Beach", "Nature", isMandatory: false, poiId: 7,
            reason: "Suggested near route");
        string output = ItineraryExplanationFallback.Resolve(item);

        output.Should().Contain("Beach").And.NotContain("CSP");
        output.Length.Should().BeLessThanOrEqualTo(ItineraryItem.FriendlyExplanationMaxLength);
        item.CspRecommendationReason.Should().Be("Suggested near route");
    }

    [Fact]
    public void PreferenceLanguageRequiresProvenPreferenceContext()
    {
        string withoutProof = ItineraryExplanationFallback.Resolve(
            Item("Cafe", "Food", isMandatory: false, poiId: 8));
        string withProof = ItineraryExplanationFallback.Resolve(
            Item("Cafe", "Food", isMandatory: false, poiId: 8,
                tags: ["coffee"], reason: "Matches preference"));

        withoutProof.Should().NotContain("sở thích");
        withProof.Should().ContainAny("sở thích", "quan tâm", "yêu thích");
    }

    [Fact]
    public void MissingMetadataAndPoiLessRestRemainSafe()
    {
        string missing = ItineraryExplanationFallback.Resolve(
            Item(null, null, isMandatory: false, poiId: null));
        string rest = ItineraryExplanationFallback.Resolve(
            Item(null, null, isMandatory: false, poiId: null, kind: ItineraryItemKind.Rest));

        missing.Should().NotBeNullOrWhiteSpace();
        rest.Should().NotBeNullOrWhiteSpace();
        new[] { missing, rest }.Should().OnlyContain(text => text.Length <= 500);
    }

    private static ItineraryExplanationItem Item(
        string? poi,
        string? category,
        bool isMandatory,
        long? poiId,
        ItineraryItemKind kind = ItineraryItemKind.Visit,
        IReadOnlyCollection<string>? tags = null,
        string reason = "") => new(
        1,
        poiId,
        poi,
        category,
        tags ?? [],
        kind,
        isMandatory,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddMinutes(30),
        30,
        null,
        null,
        reason);
}