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
}