using FluentAssertions;

using TripMate.Application.Features.TourRecommendations.Common;
using TripMate.Application.Features.TourRecommendations.Refresh;
using TripMate.Application.UnitTests.TestUtilities;
using TripMate.Domain.Entities;

namespace TripMate.Application.UnitTests.Features.TourRecommendations;

public sealed class TourRecommendationCandidateReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadBatchesAsync_Tour182InterestsHaveFullJaccardMatch()
    {
        await using var db = TestDbContext.Create();
        const long operatorId = 8;
        await TourAuthoringTestData.SeedReferenceDataAsync(db);
        await PublicTourTestData.SeedOperatorAsync(db, operatorId);

        string[] poiCategoryNames = ["Bãi biển", "Danh lam & Di sản", "Natural attraction", "Thiên nhiên"];
        string[] poiTagNames =
        [
            "Biển", "Chụp ảnh đẹp", "Đêm", "Gia đình", "Lịch sử", "Miễn phí",
            "Ngắm hoàng hôn", "Phiêu lưu", "Tâm linh", "Văn hóa",
        ];
        var pois = poiCategoryNames.Select(name => PointOfInterest.Create(
            PoiCategory.Create(name, description: null), name, 16m, 108m, operatorId, Now)).ToArray();
        foreach (var name in poiTagNames)
        {
            pois[0].AddTag(Tag.Create(name));
        }

        var tourCategory = TourCategory.Create("CULTURE_HERITAGE", "Culture & Heritage");
        db.PointsOfInterest.AddRange(pois);
        db.TourCategories.Add(tourCategory);
        await db.SaveChangesAsync();

        var tour = Tour.CreateDraft(
            operatorId, tourCategory.Id, "Miền Trung di sản 4 ngày 3 đêm",
            "Central Vietnam heritage tour", 2_000_000m, 4,
            [TourAuthoringTestData.DestinationId], "Da Nang", TourAuthoringTestData.NewPolicy(),
            pois.Select((poi, index) => TourItineraryItem.Create(
                index + 1, 1, poi.Id, new TimeOnly(9, 0), 60, null)),
            [TourSchedule.Create(Now.AddDays(10), Now.AddDays(14), "Hotel lobby", 10, Now)],
            Now);
        tour.SubmitForReview(Now);
        tour.Approve(2, Now);
        tour.Publish(2, Now);
        db.Tours.Add(tour);
        await db.SaveChangesAsync();

        var reader = new TourRecommendationCandidateReader(db);
        var candidates = new List<TourRecommendationCandidate>();
        await foreach (var batch in reader.ReadBatchesAsync(Now, null, null, CancellationToken.None))
        {
            candidates.AddRange(batch);
        }

        string[] travelerInterests =
        [
            "adventure", "beach", "culture", "heritage", "history", "nature", "photography", "relaxation",
        ];
        var candidate = candidates.Should().ContainSingle().Which;
        TourRecommendationScoring.Jaccard(travelerInterests, candidate.InterestTokens).Should().Be(1m);
        candidate.InterestTokens.Distinct().Should().BeEquivalentTo(travelerInterests);
    }

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
    public void ResolveCanonicalInterestKey_MapsFreeTextCategoryToCanonicalInterestKey()
    {
        TourRecommendationCandidateReader.ResolveCanonicalInterestKey("Food & Drink")
            .Should().Be("culinary");
    }

    [Fact]
    public void ResolveCanonicalInterestKey_MapsVietnameseAdminTagToCanonicalInterestKey()
    {
        TourRecommendationCandidateReader.ResolveCanonicalInterestKey("Ngắm bình minh")
            .Should().Be("nature");
    }

    [Theory]
    [InlineData("Culture", "culture")]
    [InlineData(" Architecture ", "architecture")]
    public void ResolveCanonicalInterestKey_RecognizesDirectKeysWithoutSynonyms(
        string rawToken, string canonicalKey)
    {
        TourRecommendationCandidateReader.ResolveCanonicalInterestKey(rawToken)
            .Should().Be(canonicalKey);
    }

    [Fact]
    public void ResolveCanonicalInterestKey_ExcludesFreeTextWithoutACanonicalMatch()
    {
        // Unmapped tags would inflate the Jaccard union without adding a possible match.
        TourRecommendationCandidateReader.ResolveCanonicalInterestKey("Chụp ảnh hoàng hôn")
            .Should().BeNull();
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
    public void ResolveCanonicalInterestKey_MapsProductionVietnameseCategoryToCanonicalInterestKey(
        string rawToken, string canonicalKey)
    {
        TourRecommendationCandidateReader.ResolveCanonicalInterestKey(rawToken)
            .Should().Be(canonicalKey);
    }

    [Theory]
    [InlineData("Bánh mì", "culinary")]
    [InlineData("Bánh xèo", "culinary")]
    [InlineData("Bún bò", "culinary")]
    [InlineData("Cao lầu", "culinary")]
    [InlineData("Chè", "culinary")]
    [InlineData("Cơm hến", "culinary")]
    [InlineData("Hải sản", "culinary")]
    [InlineData("Mì Quảng", "culinary")]
    [InlineData("Nem lụi", "culinary")]
    [InlineData("Food", "culinary")]
    [InlineData("Food Drink", "culinary")]
    [InlineData("Bảo tàng", "museum")]
    [InlineData("Di sản", "heritage")]
    [InlineData("Thắng cảnh", "heritage")]
    [InlineData("UNESCO", "heritage")]
    [InlineData("Spiritual", "heritage")]
    [InlineData("Family", "relaxation")]
    [InlineData("Entertainment", "adventure")]
    [InlineData("Vui chơi", "adventure")]
    [InlineData("Nightlife", "nature")]
    [InlineData("Về đêm", "nature")]
    public void ResolveCanonicalInterestKey_MapsProductionTagToCanonicalInterestKey(
        string rawToken, string canonicalKey)
    {
        TourRecommendationCandidateReader.ResolveCanonicalInterestKey(rawToken)
            .Should().Be(canonicalKey);
    }

    [Theory]
    [InlineData("Check in")]
    [InlineData("Must visit")]
    [InlineData("Nổi tiếng")]
    [InlineData("Sightseeing")]
    [InlineData("Lưu trú")]
    public void ResolveCanonicalInterestKey_ExcludesGenericOrUnmappedProductionTags(
        string rawToken)
    {
        TourRecommendationCandidateReader.ResolveCanonicalInterestKey(rawToken)
            .Should().BeNull();
    }

    [Fact]
    public void ResolveCanonicalInterestKey_BlankInputYieldsNoKey()
    {
        TourRecommendationCandidateReader.ResolveCanonicalInterestKey("  ")
            .Should().BeNull();
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