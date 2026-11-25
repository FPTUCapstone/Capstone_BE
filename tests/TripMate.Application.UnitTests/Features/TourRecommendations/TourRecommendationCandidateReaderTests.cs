using FluentAssertions;

using TripMate.Application.Features.TourRecommendations.Refresh;

namespace TripMate.Application.UnitTests.Features.TourRecommendations;

public sealed class TourRecommendationCandidateReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectRepresentativeSchedule_PrefersEarliestMatchingBookableSchedule()
    {
        var schedules = new[]
        {
            NewSchedule(1, new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero)),
            NewSchedule(3, new DateTimeOffset(2026, 12, 3, 1, 0, 0, TimeSpan.Zero)),
            NewSchedule(2, new DateTimeOffset(2026, 12, 3, 1, 0, 0, TimeSpan.Zero)),
        };

        var result = TourRecommendationCandidateReader.SelectRepresentativeSchedule(
            schedules,
            new DateOnly(2026, 12, 1),
            new DateOnly(2026, 12, 5));

        result!.Id.Should().Be(2);
        result.MatchesRequestedDate.Should().BeTrue();
    }

    [Fact]
    public void SelectRepresentativeSchedule_DateMismatchFallsBackWithoutFilteringTour()
    {
        var result = TourRecommendationCandidateReader.SelectRepresentativeSchedule(
            [NewSchedule(1, new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero))],
            new DateOnly(2026, 12, 1),
            new DateOnly(2026, 12, 5));

        result!.Id.Should().Be(1);
        result.MatchesRequestedDate.Should().BeFalse();
    }

    [Fact]
    public void IsBookable_RejectsPastClosedInvalidAndSoldOutSchedules()
    {
        TourRecommendationCandidateReader.IsBookable(NewSchedule(1, Now.AddDays(-1)), Now)
            .Should().BeFalse();
        TourRecommendationCandidateReader.IsBookable(NewSchedule(2, Now.AddDays(1), reserved: 10), Now)
            .Should().BeFalse();
        TourRecommendationCandidateReader.IsBookable(NewSchedule(3, Now.AddDays(1), total: 0), Now)
            .Should().BeFalse();
        TourRecommendationCandidateReader.IsBookable(NewSchedule(4, Now.AddDays(1), deadline: Now), Now)
            .Should().BeFalse();
    }

    [Fact]
    public void ResolveInterestSynonyms_MapsFreeTextCategoryToCanonicalInterestKey()
    {
        TourRecommendationCandidateReader.ResolveInterestSynonyms("Food & Drink")
            .Should().BeEquivalentTo(["Food & Drink", "culinary"]);
    }

    [Fact]
    public void ResolveInterestSynonyms_MapsVietnameseAdminTagToCanonicalInterestKey()
    {
        TourRecommendationCandidateReader.ResolveInterestSynonyms("Ngắm bình minh")
            .Should().BeEquivalentTo(["Ngắm bình minh", "nature"]);
    }

    [Fact]
    public void ResolveInterestSynonyms_KeepsAlreadyCanonicalTokenUnchangedWithNoDuplicate()
    {
        TourRecommendationCandidateReader.ResolveInterestSynonyms("Culture")
            .Should().BeEquivalentTo(["Culture"]);
    }

    [Fact]
    public void ResolveInterestSynonyms_PreservesUnmappableFreeTextTagAsIs()
    {
        TourRecommendationCandidateReader.ResolveInterestSynonyms("Chụp ảnh hoàng hôn")
            .Should().BeEquivalentTo(["Chụp ảnh hoàng hôn"]);
    }

    [Theory]
    [InlineData("Văn hóa", "culture")]
    [InlineData("Bãi biển", "beach")]
    [InlineData("Biển", "beach")]
    [InlineData("Danh lam & Di sản", "heritage")]
    [InlineData("Giải trí", "adventure")]
    [InlineData("Thiên nhiên", "nature")]
    [InlineData("Ẩm thực", "culinary")]
    [InlineData("Gia đình", "relaxation")]
    [InlineData("Miễn phí", "relaxation")]
    [InlineData("Ngắm hoàng hôn", "nature")]
    [InlineData("Phiêu lưu", "adventure")]
    [InlineData("Chụp ảnh đẹp", "photography")]
    [InlineData("Đêm", "nature")]
    [InlineData("Lịch sử", "history")]
    [InlineData("Tâm linh", "heritage")]
    [InlineData("Mua sắm", "shopping")]
    public void ResolveInterestSynonyms_MapsProductionVietnameseCategoryToCanonicalInterestKey(
        string rawToken, string canonicalKey)
    {
        TourRecommendationCandidateReader.ResolveInterestSynonyms(rawToken)
            .Should().BeEquivalentTo([rawToken, canonicalKey]);
    }

    [Theory]
    [InlineData("Kiến trúc", "architecture")]
    [InlineData("Công trình kiến trúc", "architecture")]
    [InlineData("Di tích kiến trúc", "architecture")]
    [InlineData("Kiến trúc cổ", "architecture")]
    [InlineData("Historical Architecture", "architecture")]
    [InlineData("Landmark", "architecture")]
    [InlineData("Landmark & Architecture", "architecture")]
    public void ResolveInterestSynonyms_MapsArchitectureThemedCategoryToCanonicalInterestKey(
        string rawToken, string canonicalKey)
    {
        TourRecommendationCandidateReader.ResolveInterestSynonyms(rawToken)
            .Should().BeEquivalentTo([rawToken, canonicalKey]);
    }

    [Fact]
    public void ResolveInterestSynonyms_BlankInputYieldsNoTokens()
    {
        TourRecommendationCandidateReader.ResolveInterestSynonyms("  ")
            .Should().BeEmpty();
    }

    [Fact]
    public void ParseJsonArray_ParsesOperatorEnteredJsonStringArray()
    {
        TourRecommendationCandidateReader.ParseJsonArray("""["Motorbike","Car"]""")
            .Should().Equal("Motorbike", "Car");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void ParseJsonArray_NullBlankOrMalformedJsonYieldsNoTokens(string? json)
    {
        TourRecommendationCandidateReader.ParseJsonArray(json).Should().BeEmpty();
    }

    [Fact]
    public void ParseJsonArray_SkipsBlankEntriesInTheArray()
    {
        TourRecommendationCandidateReader.ParseJsonArray("""["Vegetarian"," ","Halal",null]""")
            .Should().Equal("Vegetarian", "Halal");
    }

    private static TourRecommendationCandidateSchedule NewSchedule(
        long id,
        DateTimeOffset start,
        int total = 10,
        int reserved = 0,
        DateTimeOffset? deadline = null) =>
        new(id, start, start.AddDays(1), total, reserved, deadline);
}